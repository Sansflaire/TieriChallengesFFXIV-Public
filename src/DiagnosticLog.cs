using System;
using System.Collections.Generic;
using System.Text;

namespace TieriChallengesFFXIV;

/// <summary>
/// A bounded in-memory copy of everything this plugin logs, so a user can attach it to a bug
/// report without hunting through Dalamud's own log file.
///
/// <para>Dalamud's <c>IPluginLog</c> offers no sink to subscribe to, so the only way to capture
/// our own output is to route it through here. <see cref="Diag"/> is that route: it appends to
/// the ring buffer and forwards to <c>Plugin.Log</c>, so `/xllog` is unchanged. Anything that
/// calls <c>Plugin.Log</c> directly is invisible to a bug report — use <see cref="Diag"/>.</para>
///
/// <para><b>That rule was stated here and then ignored 141 times.</b> A review on 2026-08-26 found
/// only 19 of 160 log calls went through <see cref="Diag"/>, and the ones that did were the least
/// useful — toast bookkeeping and fly-text — while every call that answers "why didn't my challenge
/// fire", "why did my sync report nothing" and "why is my cue silent" went to <c>Plugin.Log</c> and
/// never reached a report. A player ticking "attach my log" got an environment header and almost
/// nothing else. Everything diagnostic is routed through <see cref="Diag"/> now.</para>
///
/// <para><b>What is deliberately still on <c>Plugin.Log</c>:</b> the dev-only tools (BanAdmin,
/// ChallengeExporter), and <c>GameSound</c>'s per-cue success chatter — it logs a long line on
/// every cue, and a 400-entry ring filled with "wav playing" would evict the failures this exists
/// to carry. <c>GameSound</c>'s warnings and errors DO go through <see cref="Diag"/>, which is the
/// half that matters in a report.</para>
///
/// <para>Bounded on purpose: a fixed number of entries, each truncated. A plugin that leaks
/// memory through its own diagnostics would be a poor joke.</para>
/// </summary>
public static class DiagnosticLog
{
    /// <summary>Entries retained. ~40 KB worst case.</summary>
    private const int Capacity = 400;

    /// <summary>Longest single entry kept; anything larger is truncated rather than dropped.</summary>
    private const int MaxEntryLength = 400;

    private static readonly object Gate = new();
    private static readonly Queue<string> Entries = new(Capacity);

    public static int Count
    {
        get { lock (Gate) return Entries.Count; }
    }

    internal static void Append(string level, string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (message.Length > MaxEntryLength) message = message.Substring(0, MaxEntryLength) + "…";

        string line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";

        lock (Gate)
        {
            if (Entries.Count >= Capacity) Entries.Dequeue();
            Entries.Enqueue(line);
        }
    }

    public static void Clear()
    {
        lock (Gate) Entries.Clear();
    }

    /// <summary>The whole buffer, oldest first.</summary>
    public static string Dump()
    {
        lock (Gate)
        {
            var sb = new StringBuilder(Entries.Count * 80);
            foreach (var line in Entries) sb.AppendLine(line);
            return sb.ToString();
        }
    }

    /// <summary>The most recent <paramref name="count"/> entries, for an on-screen preview.</summary>
    public static string Tail(int count)
    {
        lock (Gate)
        {
            var all = Entries.ToArray();
            int start = Math.Max(0, all.Length - count);
            var sb = new StringBuilder();
            for (int i = start; i < all.Length; i++) sb.AppendLine(all[i]);
            return sb.ToString();
        }
    }

    /// <summary>
    /// State worth having in every report: what build, which renderer, sync state, counts. Kept
    /// deliberately free of anything identifying — no character name, no world, no account id.
    /// The zone is included because almost every challenge is zone-gated, so "it didn't fire" is
    /// unanswerable without it.
    /// </summary>
    public static string BuildEnvironmentReport(Configuration config, CompletionStore store)
    {
        var sb = new StringBuilder();
        try
        {
            sb.AppendLine($"Plugin      : {PluginVersion.DisplayLong} ({(Plugin.IsDevBuild ? "DEV" : "public")} build)");
            sb.AppendLine($"Renderer    : {(config.UsePanacheUI ? "PanacheUI" : "ImGui fallback")}"
                        + $" (library {(PanacheAvailability.IsAvailable ? "available" : "UNAVAILABLE: " + PanacheAvailability.FailureReason)})");

            var (done, total) = ChallengeCatalog.OverallProgress(config, store);
            sb.AppendLine($"Challenges  : {total} total, {done} complete");
            sb.AppendLine($"Official    : {(ChallengeCatalog.Official?.Count ?? 0)} synced, "
                        + $"{config.CustomChallenges.Count} local");
            sb.AppendLine($"Last sync   : {(config.LastSyncUtc == DateTime.MinValue ? "never" : config.LastSyncUtc.ToString("u"))}");
            sb.AppendLine($"Completions : {store.CurrentCount} current, {store.PermanentCount} permanent");

            try
            {
                ushort t = (ushort)Plugin.ClientState.TerritoryType;
                sb.AppendLine($"Zone        : {(t == 0 ? "—" : $"{PlayerStateReader.ZoneName(t)} ({t})")}");
                sb.AppendLine($"Logged in   : {Plugin.ClientState.IsLoggedIn}");
            }
            catch { sb.AppendLine("Zone        : unavailable"); }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(environment report failed: {ex.Message})");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Logging facade. Writes to <see cref="DiagnosticLog"/> AND to Dalamud, so everything visible in
/// `/xllog` is also attachable to a bug report. Prefer this over <c>Plugin.Log</c> everywhere.
/// </summary>
public static class Diag
{
    public static void Info(string message)
    {
        DiagnosticLog.Append("INF", message);
        Plugin.Log.Information(message);
    }

    public static void Debug(string message)
    {
        DiagnosticLog.Append("DBG", message);
        Plugin.Log.Debug(message);
    }

    public static void Warn(string message)
    {
        DiagnosticLog.Append("WRN", message);
        Plugin.Log.Warning(message);
    }

    public static void Error(string message)
    {
        DiagnosticLog.Append("ERR", message);
        Plugin.Log.Error(message);
    }

    public static void Error(Exception ex, string message)
    {
        // The exception type and message matter far more in a report than the stack, which is
        // usually truncated anyway — but keep the first frames, they are what identifies it.
        DiagnosticLog.Append("ERR", $"{message} :: {ex.GetType().Name}: {ex.Message}");

        string? stack = ex.StackTrace;
        if (!string.IsNullOrEmpty(stack))
        {
            var lines = stack.Split('\n');
            for (int i = 0; i < Math.Min(3, lines.Length); i++)
                DiagnosticLog.Append("ERR", "  " + lines[i].Trim());
        }

        Plugin.Log.Error(ex, message);
    }
}
