using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using Newtonsoft.Json;

namespace TieriChallengesFFXIV;

/// <summary>Where a challenge came from. Drives the badge shown in the list.</summary>
public enum ChallengeSource
{
    /// <summary>Downloaded from the public repo and present in its signed master list.</summary>
    Official = 0,

    /// <summary>Authored locally. Can never masquerade as Official — see <see cref="OfficialCatalog"/>.</summary>
    Custom = 1,
}

/// <summary>One row of the repo's master list.</summary>
public sealed class MasterEntry
{
    /// <summary>The challenge's permanent GUID. Identical for every user who has this challenge.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Path within the repo, e.g. <c>challenges/&lt;guid&gt;.json</c>.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>SHA-256 of the challenge file's bytes. Content that does not match is rejected.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Bumped by the author when a challenge's definition changes.</summary>
    public int Revision { get; set; } = 1;

    // Informational, so the UI can list what is available before downloading.
    public string Title    { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int    SortOrder { get; set; }
}

/// <summary>The repo's master list. The authority on which GUIDs are official.</summary>
public sealed class MasterList
{
    public int    SchemaVersion { get; set; } = 1;
    public string Generated     { get; set; } = string.Empty;
    public List<MasterEntry> Challenges { get; set; } = new();

    /// <summary>
    /// The published category list, in display order. Categories travel with the catalogue rather
    /// than being compiled into the plugin, so adding one needs a publish, not a release.
    ///
    /// <para><b>This does NOT bump SchemaVersion.</b> The sync service refuses any master list
    /// claiming a schema above the one it knows, so raising it would make every already-installed
    /// plugin reject the whole catalogue over an added field. Newtonsoft ignores properties a
    /// build does not know about, which is exactly the compatibility this relies on: older
    /// plugins keep working and simply derive categories from the challenges as before.</para>
    /// </summary>
    public List<string> Categories { get; set; } = new();
}

/// <summary>
/// The synced official challenge set, cached on disk.
///
/// <para><b>The trust rule.</b> A challenge is Official if and only if its GUID appears in the
/// master list downloaded from the repo AND the file's SHA-256 matches what the master list
/// says. Anything else — including a locally authored challenge that has been hand-edited to
/// claim an official GUID — is Custom. Official definitions are stored in their own directory
/// and are never merged into <c>Configuration.CustomChallenges</c>, so a local file can never
/// overwrite an official one.</para>
///
/// <para>Completion is keyed by GUID regardless of source, which is the point of the shared
/// GUIDs: finishing an official challenge means the same thing on every install.</para>
/// </summary>
public sealed class OfficialCatalog
{
    private const string MasterFileName = "master.json";
    private const string CacheDirName   = "official";

    private readonly string _dir;
    private readonly string _masterPath;

    private MasterList _master = new();
    private readonly List<CustomChallenge> _challenges = new();
    private readonly HashSet<string> _officialIds = new(StringComparer.OrdinalIgnoreCase);

    public OfficialCatalog(string configDirectory)
    {
        _dir        = Path.Combine(configDirectory, CacheDirName);
        _masterPath = Path.Combine(_dir, MasterFileName);
    }

    public string CacheDirectory => _dir;
    public string MasterPath     => _masterPath;

    public IReadOnlyList<CustomChallenge> Challenges => _challenges;
    public int Count => _challenges.Count;

    /// <summary>Everything the master list claims exists, whether or not it is downloaded yet.</summary>
    public IReadOnlyList<MasterEntry> MasterEntries => _master.Challenges;

    /// <summary>
    /// Published categories in their published order. Empty on a catalogue from before categories
    /// were published, which is why the display list still folds in whatever the challenges name.
    /// </summary>
    public IReadOnlyList<string> Categories => _master.Categories ?? new List<string>();

    public string GeneratedAt => _master.Generated;

    /// <summary>The trust test. Only GUIDs from the master list are official.</summary>
    public bool IsOfficial(string id) =>
        !string.IsNullOrEmpty(id) && _officialIds.Contains(id);

    // ── Load ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reload the cached master list and every challenge file it vouches for. A file whose hash
    /// does not match the master is skipped and logged — a mismatch means tampering or a partial
    /// download, and either way it must not be treated as official.
    /// </summary>
    public void Load()
    {
        _master = new MasterList();
        _challenges.Clear();
        _officialIds.Clear();

        try
        {
            if (!File.Exists(_masterPath))
            {
                Diag.Info("[Official] no synced catalogue yet.");
                return;
            }

            var parsed = JsonConvert.DeserializeObject<MasterList>(File.ReadAllText(_masterPath));
            if (parsed?.Challenges == null)
            {
                Diag.Warn("[Official] master list unreadable; treating as empty.");
                return;
            }
            _master = parsed;

            int loaded = 0, rejected = 0;
            foreach (var entry in _master.Challenges)
            {
                if (string.IsNullOrWhiteSpace(entry.Id)) continue;

                string path = Path.Combine(_dir, entry.Id + ".json");
                if (!File.Exists(path)) continue;          // in the list but not downloaded yet

                byte[] bytes = File.ReadAllBytes(path);
                if (!string.IsNullOrWhiteSpace(entry.Sha256)
                    && !string.Equals(Sha256Hex(bytes), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Diag.Warn($"[Official] hash mismatch for {entry.Id}; ignoring the cached copy.");
                    rejected++;
                    continue;
                }

                var challenge = JsonConvert.DeserializeObject<CustomChallenge>(
                    Encoding.UTF8.GetString(bytes));
                if (challenge == null || string.IsNullOrWhiteSpace(challenge.Id)) continue;

                // The master list is the authority on identity: if the file disagrees with the
                // entry it was fetched for, trust the entry.
                challenge.Id = entry.Id;

                _challenges.Add(challenge);
                _officialIds.Add(entry.Id);
                loaded++;
            }

            Diag.Info(
                $"[Official] loaded {loaded} official challenge(s)"
              + (rejected > 0 ? $", rejected {rejected} on hash mismatch." : "."));
        }
        catch (Exception ex)
        {
            Diag.Error(ex, "[Official] failed to load the synced catalogue");
        }
    }

    // ── Write (used by the sync service) ─────────────────────────────────────

    public void EnsureDirectory()
    {
        try { Directory.CreateDirectory(_dir); }
        catch (Exception ex) { Diag.Error(ex, "[Official] could not create the cache directory"); }
    }

    public string PathFor(string id) => Path.Combine(_dir, id + ".json");

    public void WriteMaster(string json)
    {
        EnsureDirectory();
        AtomicWrite(_masterPath, json);
    }

    public void WriteChallenge(string id, string json)
    {
        EnsureDirectory();
        AtomicWrite(PathFor(id), json);
    }

    /// <summary>Temp file then replace, so an interrupted sync cannot leave a half-written file.</summary>
    private static void AtomicWrite(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else                   File.Move(tmp, path);
    }

    /// <summary>Remove cached files the master list no longer mentions.</summary>
    public int PruneOrphans(HashSet<string> keepIds)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(_dir)) return 0;

            foreach (string file in Directory.GetFiles(_dir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (string.Equals(name, "master", StringComparison.OrdinalIgnoreCase)) continue;
                if (keepIds.Contains(name)) continue;

                File.Delete(file);
                removed++;
            }
        }
        catch (Exception ex)
        {
            Diag.Error(ex, "[Official] prune failed");
        }
        return removed;
    }

    public static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public static string Sha256Hex(string text) => Sha256Hex(new UTF8Encoding(false).GetBytes(text));
}
