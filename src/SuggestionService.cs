using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Newtonsoft.Json;

namespace TieriChallengesFFXIV;

/// <summary>
/// Sends player suggestions to a Discord channel.
///
/// <para><b>The endpoint is baked into the shipped DLL, so treat it as public.</b> Anyone can
/// extract a string constant from an assembly. That is why <see cref="Endpoint"/> should point at
/// a proxy you control (a Cloudflare Worker holding the real webhook), NOT at a raw
/// <c>discord.com/api/webhooks/...</c> URL. With a proxy you can rate-limit, block, and rotate
/// the underlying webhook without shipping a new build. With a raw webhook, the only remedy for
/// abuse is deleting the webhook, which breaks every copy of the plugin already installed.
/// See docs/Discord Suggestions Setup.md.</para>
///
/// <para>Client-side limits here are courtesy, not security — an attacker with the URL ignores
/// them entirely. They exist to stop an honest user accidentally spamming the channel.</para>
/// </summary>
public static class SuggestionService
{
    /// <summary>
    /// Where suggestions are POSTed, baked in at build time from the gitignored
    /// <c>src/Secrets.props</c> and read back out of assembly metadata. Deliberately NOT a
    /// literal in source, so the credential never enters git history.
    ///
    /// Empty (a fresh clone with no Secrets.props) = feature disables itself with an explanation.
    /// Shipping a dead button would be worse than shipping no button.
    /// </summary>
    public static readonly string Endpoint = ResolveEndpoint();

    private static string ResolveEndpoint()
    {
        try
        {
            foreach (var attr in typeof(SuggestionService).Assembly
                         .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
            {
                if (attr is System.Reflection.AssemblyMetadataAttribute meta
                    && meta.Key == "SuggestionEndpoint"
                    && !string.IsNullOrWhiteSpace(meta.Value))
                {
                    return meta.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Error(ex, "[Suggestion] could not resolve endpoint");
        }
        return string.Empty;
    }

    /// <summary>Discord hard-caps message content at 2000 characters; stay well under.</summary>
    public const int MaxMessageLength = 1500;
    public const int MaxContactLength = 100;

    /// <summary>Courtesy limits, per game session.</summary>
    private const int    MaxPerSession   = 5;
    private const double CooldownSeconds = 60;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static int      _sentThisSession;
    private static DateTime _lastSendUtc = DateTime.MinValue;

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>
    /// The sending character as <c>Name@World</c>, or null when not logged in.
    ///
    /// <para>This is the identity every report carries, and the same string <see cref="BanService"/>
    /// hashes — deliberately one function, so the name that can be banned is always exactly the name
    /// that was sent. Two separate formatters would drift and quietly stop matching.</para>
    /// </summary>
    public static string? CurrentSender()
    {
        try
        {
            var pc = Plugin.ObjectTable.LocalPlayer;
            if (pc == null) return null;

            string name  = pc.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) return null;

            string world = pc.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(world) ? name : $"{name}@{world}";
        }
        catch
        {
            // Never let a report fail because the world sheet was unavailable for a frame.
            return null;
        }
    }


    public static int SendsRemaining => Math.Max(0, MaxPerSession - _sentThisSession);

    /// <summary>Seconds still to wait, or 0 if ready.</summary>
    public static double CooldownRemaining
    {
        get
        {
            if (_lastSendUtc == DateTime.MinValue) return 0;
            double elapsed = (DateTime.UtcNow - _lastSendUtc).TotalSeconds;
            return Math.Max(0, CooldownSeconds - elapsed);
        }
    }

    /// <summary>Why sending is currently refused, or null when it is allowed.</summary>
    public static string? BlockedReason(string message)
    {
        if (!IsConfigured)                      return "Suggestions are not configured in this build.";
        if (string.IsNullOrWhiteSpace(message)) return "Type a suggestion first.";
        if (message.Trim().Length < 10)         return "Please write a little more — at least 10 characters.";
        if (_sentThisSession >= MaxPerSession)  return $"You've sent {MaxPerSession} this session. Thanks! Restart the game to send more.";

        double wait = CooldownRemaining;
        if (wait > 0) return $"Please wait {Math.Ceiling(wait):0} more second(s) before sending again.";

        return null;
    }

    /// <summary>
    /// Fire the suggestion. Never throws — returns a human-readable result for the UI.
    ///
    /// Runs entirely off the game thread; the caller does not await it on the framework tick.
    /// </summary>
    public static async Task<(bool ok, string message)> SendAsync(
        string message, string? contact, string? characterName)
    {
        string? blocked = BlockedReason(message);
        if (blocked != null) return (false, blocked);

        // Optimistically consume the budget so a double-click cannot double-send while the
        // request is in flight.
        _sentThisSession++;
        _lastSendUtc = DateTime.UtcNow;

        try
        {
            string trimmed = message.Trim();
            if (trimmed.Length > MaxMessageLength) trimmed = trimmed.Substring(0, MaxMessageLength);

            var sb = new StringBuilder();
            sb.AppendLine("**New suggestion**");
            sb.AppendLine($"`{PluginVersion.DisplayLong}` · `{(Plugin.IsDevBuild ? "DEV" : "public")} build`");

            if (!string.IsNullOrWhiteSpace(characterName))
                sb.AppendLine($"From: **{Sanitize(characterName!, 60)}**");

            if (!string.IsNullOrWhiteSpace(contact))
                sb.AppendLine($"Contact: {Sanitize(contact!, MaxContactLength)}");

            sb.AppendLine();
            sb.AppendLine(Sanitize(trimmed, MaxMessageLength));

            // Discord's webhook shape. A proxy is expected to forward this body unchanged.
            var payload = new
            {
                username = "Challenges Suggestions",
                content  = sb.ToString(),
                // Belt and braces: never let a suggestion ping anyone, whatever it contains.
                allowed_mentions = new { parse = Array.Empty<string>() },

                // Ours, not Discord's — the relay strips it before forwarding. It is what lets the
                // ban check run SERVER-side, where a hand-built client cannot skip it. Discord
                // would reject this unknown field, which is the tell that the endpoint must be the
                // relay and never the webhook directly.
                sender = characterName ?? string.Empty,
            };

            string json = JsonConvert.SerializeObject(payload);
            using var body = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await Http.PostAsync(Endpoint, body).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                Diag.Info("[Suggestion] delivered.");
                return (true, "Sent — thank you!");
            }

            // Refund the budget: a failed send should not cost the user an attempt.
            _sentThisSession--;
            Diag.Warn($"[Suggestion] endpoint returned {(int)response.StatusCode}.");
            return (false, $"Couldn't send (server said {(int)response.StatusCode}). Try again later.");
        }
        catch (TaskCanceledException)
        {
            _sentThisSession--;
            return (false, "Timed out. Check your connection and try again.");
        }
        catch (Exception ex)
        {
            _sentThisSession--;
            Diag.Error(ex, "[Suggestion] send failed");
            return (false, "Couldn't send. Check your connection and try again.");
        }
    }

    /// <summary>
    /// Send a bug report: the user's message plus the plugin's own log and an environment
    /// snapshot, attached as a text file.
    ///
    /// <para>The log goes as an <b>attachment</b> rather than in the message body because
    /// Discord caps content at 2000 characters and a useful log is far longer than that;
    /// truncating it to fit would throw away exactly the part that explains the failure.</para>
    ///
    /// <para>Note this uses multipart/form-data. A proxy sitting in front of the webhook must
    /// forward the body and content-type unchanged — see docs/Discord Suggestions Setup.md.</para>
    /// </summary>
    public static async Task<(bool ok, string message)> SendBugReportAsync(
        string message, string? contact, string? characterName, string logText)
    {
        string? blocked = BlockedReason(message);
        if (blocked != null) return (false, blocked);

        _sentThisSession++;
        _lastSendUtc = DateTime.UtcNow;

        try
        {
            string trimmed = message.Trim();
            if (trimmed.Length > MaxMessageLength) trimmed = trimmed.Substring(0, MaxMessageLength);

            var sb = new StringBuilder();
            sb.AppendLine("**🐛 Bug report**");
            sb.AppendLine($"`{PluginVersion.DisplayLong}` · `{(Plugin.IsDevBuild ? "DEV" : "public")} build`");

            if (!string.IsNullOrWhiteSpace(characterName))
                sb.AppendLine($"From: **{Sanitize(characterName!, 60)}**");
            if (!string.IsNullOrWhiteSpace(contact))
                sb.AppendLine($"Contact: {Sanitize(contact!, MaxContactLength)}");

            sb.AppendLine();
            sb.AppendLine(Sanitize(trimmed, MaxMessageLength));
            sb.AppendLine();
            sb.AppendLine("_Log attached._");

            var payload = new
            {
                username = "Challenges Bug Reports",
                content  = sb.ToString(),
                allowed_mentions = new { parse = Array.Empty<string>() },
            };

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"),
                     "payload_json");

            // Same purpose as the `sender` field on the suggestion payload: it lets the relay run
            // the ban check server-side. A separate form field rather than a JSON property because
            // this request is multipart, and the relay deletes it before forwarding to Discord.
            form.Add(new StringContent(characterName ?? string.Empty, Encoding.UTF8), "sender");

            var logBytes = Encoding.UTF8.GetBytes(
                string.IsNullOrWhiteSpace(logText) ? "(log was empty)" : logText);
            var file = new ByteArrayContent(logBytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            form.Add(file, "files[0]", $"challenges-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt");

            var response = await Http.PostAsync(Endpoint, form).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                Diag.Info("[Report] bug report delivered.");
                return (true, "Report sent — thank you!");
            }

            _sentThisSession--;
            Diag.Warn($"[Report] endpoint returned {(int)response.StatusCode}.");
            return (false, $"Couldn't send (server said {(int)response.StatusCode}). Try again later.");
        }
        catch (TaskCanceledException)
        {
            _sentThisSession--;
            return (false, "Timed out. Check your connection and try again.");
        }
        catch (Exception ex)
        {
            _sentThisSession--;
            Diag.Error(ex, "[Report] send failed");
            return (false, "Couldn't send. Check your connection and try again.");
        }
    }

    /// <summary>
    /// Neutralise Discord markdown and mention syntax so a suggestion cannot format the channel,
    /// impersonate a heading, or smuggle a mass ping through.
    /// </summary>
    private static string Sanitize(string input, int max)
    {
        if (input.Length > max) input = input.Substring(0, max);

        var sb = new StringBuilder(input.Length + 16);
        foreach (char c in input)
        {
            switch (c)
            {
                case '`': case '*': case '_': case '~': case '>': case '|':
                case '@': case '#': case '\\':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
