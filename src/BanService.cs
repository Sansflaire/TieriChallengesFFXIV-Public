using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using Newtonsoft.Json;

namespace TieriChallengesFFXIV;

/// <summary>
/// Ban enforcement. <b>Every part of the ban system lives in this one file</b>, deliberately, so
/// that excluding it from a public source tree is a single <c>&lt;Compile Remove&gt;</c> and never a
/// hunt through the codebase for stray references.
///
/// <para><b>What this is and is not.</b> It is obfuscation and friction, not security. The plugin
/// ships as a .NET assembly, so the salts below and this whole algorithm can be recovered by anyone
/// willing to run a decompiler, and someone who builds their own plugin bypasses it entirely. What
/// it does buy: the published file leaks nothing to a casual reader, and evading a ban stops being
/// something a normal person can do by accident or by editing a JSON file.</para>
///
/// <para><b>Why hashes rather than an encoded list.</b> A reversible encoding means one decompile
/// yields the entire roster of banned characters forever — real names, publishable, permanently.
/// A salted hash can only ever answer "is THIS identity banned?", one guess at a time. The ban
/// reason is separately encrypted under a key derived from the identity it belongs to, so only the
/// banned player's own client can read their own message and a reader of the file learns neither
/// who is listed nor why.</para>
///
/// <para><b>Fail-open, deliberately.</b> Every network and parse failure leaves the previously
/// cached verdict in place and, absent a cache, treats the player as not banned. A plugin that
/// bricks itself because GitHub was briefly unreachable would punish everyone to inconvenience one
/// person. The cache is what makes that safe: once seen, a ban survives going offline.</para>
/// </summary>
internal static class BanService
{
    // ── Tuning ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Public sync repo holding <c>bans.json</c>. Raw content, so no API token is involved and the
    /// file is cacheable by GitHub's CDN.
    /// </summary>
    /// <summary>
    /// Preferred source. The contents API serves the file at HEAD immediately.
    ///
    /// <para><b>Measured 2026-08-24:</b> raw.githubusercontent served the PRE-publish list for over
    /// a minute after a push, cache-busting query string and all — the same staleness already
    /// documented in <see cref="ChallengeSyncService"/>. For challenges that is an annoyance; for a
    /// ban it is a window in which the ban does not exist, so this path is tried first.</para>
    ///
    /// <para>Unauthenticated, so it is rate-limited to 60/hour per IP. One fetch per plugin load is
    /// nowhere near that, and exceeding it merely falls through to the raw URL below.</para>
    /// </summary>
    private const string BansApiUrl =
        "https://api.github.com/repos/Sansflaire/TieriChallengesFFXIV-Sync/contents/bans.json";

    /// <summary>Fallback: no rate limit, but can be up to ~5 minutes stale.</summary>
    private const string BansRawUrl =
        "https://raw.githubusercontent.com/Sansflaire/TieriChallengesFFXIV-Sync/main/bans.json";

    /// <summary>
    /// Domain separation for the two derived values. They MUST differ: deriving the lookup hash and
    /// the reason key from the same input would publish the decryption key next to the ciphertext.
    /// </summary>
    private const string IdSalt     = "tc:v1:identity:8f13a2c7d0e94b16";
    private const string ReasonSalt = "tc:v1:reason:5b0ac91e37d8462f";

    private const int NonceLen = 12;   // AES-GCM standard
    private const int TagLen   = 16;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // ── State ────────────────────────────────────────────────────────────────

    /// <summary>True once a check has positively matched the logged-in character.</summary>
    public static bool IsBanned { get; private set; }

    /// <summary>The decrypted reason shown to the banned player. Never empty when banned.</summary>
    public static string Reason { get; private set; } = string.Empty;

    /// <summary>Identity the current verdict applies to, so a character switch re-evaluates.</summary>
    private static string _verdictFor = string.Empty;

    private static List<BanEntry> _entries = new();
    private static string _cachePath = string.Empty;

    // ── Wire format ──────────────────────────────────────────────────────────

    private sealed class BanFile
    {
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = 1;
        [JsonProperty("entries")]       public List<BanEntry> Entries { get; set; } = new();
    }

    private sealed class BanEntry
    {
        /// <summary>Base64 SHA-256 of the salted identity.</summary>
        [JsonProperty("h")] public string Hash { get; set; } = string.Empty;

        /// <summary>Base64 of nonce ‖ tag ‖ ciphertext for the reason.</summary>
        [JsonProperty("r")] public string Reason { get; set; } = string.Empty;
    }

    // ── Identity ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The canonical string every derivation is built from. Lower-cased and trimmed so that
    /// "Tieri@Balmung" typed into the ban form matches what the game reports, whatever the casing.
    /// </summary>
    public static string Identity(string name, string world) =>
        $"{(name ?? string.Empty).Trim()}@{(world ?? string.Empty).Trim()}".ToLowerInvariant();

    public static string HashOf(string identity) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(identity + IdSalt)));

    private static byte[] KeyOf(string identity) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(identity + ReasonSalt));

    // ── Reason crypto ────────────────────────────────────────────────────────

    /// <summary>Encrypt a reason under the identity it belongs to. Returns base64 nonce‖tag‖ct.</summary>
    public static string EncryptReason(string identity, string reason)
    {
        byte[] key   = KeyOf(identity);
        byte[] plain = Encoding.UTF8.GetBytes(reason ?? string.Empty);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLen);
        byte[] tag   = new byte[TagLen];
        byte[] ct    = new byte[plain.Length];

        using var gcm = new AesGcm(key, TagLen);
        gcm.Encrypt(nonce, plain, ct, tag);

        var packed = new byte[NonceLen + TagLen + ct.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceLen);
        Buffer.BlockCopy(tag,   0, packed, NonceLen, TagLen);
        Buffer.BlockCopy(ct,    0, packed, NonceLen + TagLen, ct.Length);
        return Convert.ToBase64String(packed);
    }

    /// <summary>
    /// Decrypt a reason. Returns null on any failure — wrong identity, truncated blob, tampered
    /// ciphertext. A failed decrypt must never throw into a draw loop.
    /// </summary>
    public static string? DecryptReason(string identity, string packedB64)
    {
        try
        {
            byte[] packed = Convert.FromBase64String(packedB64);
            if (packed.Length < NonceLen + TagLen) return null;

            var nonce = new byte[NonceLen];
            var tag   = new byte[TagLen];
            var ct    = new byte[packed.Length - NonceLen - TagLen];
            Buffer.BlockCopy(packed, 0, nonce, 0, NonceLen);
            Buffer.BlockCopy(packed, NonceLen, tag, 0, TagLen);
            Buffer.BlockCopy(packed, NonceLen + TagLen, ct, 0, ct.Length);

            var plain = new byte[ct.Length];
            using var gcm = new AesGcm(KeyOf(identity), TagLen);
            gcm.Decrypt(nonce, ct, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    // ── Authoring (dev tooling calls this) ───────────────────────────────────

    /// <summary>Build the publishable file from plaintext records. Dev-side only.</summary>
    public static string BuildBansJson(IEnumerable<(string Name, string World, string Reason)> records)
    {
        var file = new BanFile();
        foreach (var (name, world, reason) in records)
        {
            string id = Identity(name, world);
            file.Entries.Add(new BanEntry { Hash = HashOf(id), Reason = EncryptReason(id, reason) });
        }

        return JsonConvert.SerializeObject(file, Formatting.Indented);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Load the cached list immediately, then refresh from the network in the background.
    ///
    /// <para>Cache first so a ban is enforced on the very first frame of a session that starts
    /// offline; refresh after so an unban lands without the player doing anything.</para>
    /// </summary>
    public static void Initialise(string configDir)
    {
        _cachePath = Path.Combine(configDir, "bans-cache.json");

        try
        {
            if (File.Exists(_cachePath))
            {
                var cached = JsonConvert.DeserializeObject<BanFile>(File.ReadAllText(_cachePath));
                if (cached?.Entries != null) _entries = cached.Entries;
            }
        }
        catch (Exception ex)
        {
            Diag.Debug($"[Ban] cache unreadable: {ex.Message}");
        }

        _ = Task.Run(RefreshAsync);
    }

    /// <summary>
    /// Pull the published list. Every failure path keeps the existing list — "could not reach the
    /// server" must never read as "nobody is banned", which is what starting from empty would do.
    /// </summary>
    public static async Task RefreshAsync()
    {
        try
        {
            string? json = await TryFetchAsync(BansApiUrl, api: true).ConfigureAwait(false)
                        ?? await TryFetchAsync($"{BansRawUrl}?t={DateTime.UtcNow.Ticks}", api: false)
                                 .ConfigureAwait(false);

            if (json == null) return;

            var file = JsonConvert.DeserializeObject<BanFile>(json);
            if (file?.Entries == null) return;
            if (file.SchemaVersion > 1)
            {
                Diag.Warn($"[Ban] list schema {file.SchemaVersion} is newer than this build understands.");
                return;
            }

            _entries = file.Entries;

            try { File.WriteAllText(_cachePath, json); }
            catch (Exception ex) { Diag.Debug($"[Ban] cache write failed: {ex.Message}"); }

            // Invalidate the standing verdict so the next frame re-checks it. A ban issued
            // mid-session still takes hold without a relog — Plugin.DrawUI calls Evaluate() every
            // frame, and clearing this is exactly what makes that call stop short-circuiting.
            //
            // Deliberately NOT calling Evaluate() here. This method runs on a background thread
            // (Task.Run in Initialise), and Evaluate reads ObjectTable.LocalPlayer, which is live
            // game memory and main-thread-only — the same rule InventoryWatcher.Count documents.
            // Off-thread it can tear or fault, and doing it inside the ban path means the failure
            // mode is a crash on the frame someone gets banned.
            _verdictFor = string.Empty;
        }
        catch (Exception ex)
        {
            // Includes the 404 that means "no bans published", the normal state.
            Diag.Debug($"[Ban] list not refreshed: {ex.Message}");
        }
    }

    /// <summary>
    /// One fetch attempt. Returns null on any failure so the caller can fall through to the next
    /// source — never throws, and never distinguishes "no bans published" (404) from a real error,
    /// because both must leave the existing list untouched.
    /// </summary>
    private static async Task<string?> TryFetchAsync(string url, bool api)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            // GitHub's API rejects requests without a User-Agent, and `raw` asks it for the file
            // itself rather than the JSON envelope with base64 content.
            req.Headers.Add("User-Agent", "TieriChallengesFFXIV");
            if (api) req.Headers.Add("Accept", "application/vnd.github.raw");

            var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Diag.Debug($"[Ban] {(api ? "api" : "raw")} returned {(int)resp.StatusCode}.");
                return null;
            }

            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diag.Debug($"[Ban] {(api ? "api" : "raw")} fetch failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Re-check the logged-in character against the list. Cheap and idempotent: it short-circuits
    /// unless the identity actually changed, so calling it from a framework tick is fine.
    /// </summary>
    public static void Evaluate()
    {
        string identity;
        try
        {
            var pc = Plugin.ObjectTable.LocalPlayer;
            if (pc == null)
            {
                // Not logged in yet. Deliberately does NOT clear a standing verdict — logging out
                // must not be a way to shake off the ban banner mid-session.
                return;
            }

            string world = pc.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty;
            identity = Identity(pc.Name.ToString(), world);
        }
        catch
        {
            return;
        }

        if (identity == _verdictFor) return;
        _verdictFor = identity;

        string hash = HashOf(identity);
        foreach (var entry in _entries)
        {
            if (!string.Equals(entry.Hash, hash, StringComparison.Ordinal)) continue;

            IsBanned = true;
            Reason   = DecryptReason(identity, entry.Reason)
                       ?? "No reason was recorded.";
            Diag.Info("[Ban] this character is on the ban list.");
            return;
        }

        IsBanned = false;
        Reason   = string.Empty;
    }
}
