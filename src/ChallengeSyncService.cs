using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Newtonsoft.Json;

namespace TieriChallengesFFXIV;

public sealed class SyncResult
{
    public bool   Ok       { get; init; }
    public string Message  { get; init; } = string.Empty;
    public int    Added    { get; init; }
    public int    Updated  { get; init; }
    public int    Removed  { get; init; }
    public int    Rejected { get; init; }
}

/// <summary>
/// Downloads the official challenge catalogue from the public repo.
///
/// <para><b>What makes a challenge official.</b> The repo's <c>master.json</c> is the authority.
/// It lists every official challenge by GUID with a SHA-256 of its file. The plugin downloads
/// only what the master list names, verifies each file against that hash, and treats a mismatch
/// as a rejection rather than a warning. A GUID that is not in the master list is not official —
/// which is exactly what stops a locally authored challenge from claiming to be one.</para>
///
/// <para>Everything here runs off the game thread and is bounded: a fixed source, a capped
/// number of files per sync, per-request timeouts, and no retry storms.</para>
/// </summary>
public sealed class ChallengeSyncService
{
    /// <summary>
    /// The public sync repo — challenge data and moderation data together. NOT the release repo
    /// that users add to Dalamud: that one holds only releases and pluginmaster.json, so a plugin
    /// update and a challenge update no longer share a publish cadence.
    /// </summary>
    private const string RepoRawBase =
        "https://raw.githubusercontent.com/Sansflaire/TieriChallengesFFXIV-Sync/main/";

    private const string MasterPath = "challenges/master.json";


    /// <summary>
    /// raw.githubusercontent serves <c>Cache-Control: max-age=300</c>, so for five minutes after
    /// a publish an edge can still hand out the OLD file. A real user hit exactly that: a sync 21
    /// seconds after a publish returned the pre-publish master list and reported zero challenges,
    /// with nothing wrong anywhere.
    ///
    /// <b>This is a mitigation, not a cure.</b> Measured 2026-08-23: a cache-busted request one
    /// minute after a publish STILL returned the pre-publish file, so raw.githubusercontent
    /// appears to cache by path and ignore the query string. The parameter is kept because it is
    /// free and may help on some edges, but do not rely on it — a publish can take up to five
    /// minutes to become visible, and the UI says so rather than pretending otherwise.
    /// </summary>
    private static string Bust(string path) =>
        RepoRawBase + path + "?t=" + DateTime.UtcNow.Ticks.ToString("x");

    /// <summary>
    /// Hard ceiling on downloads per sync. The master list is ours, but a bounded loop over
    /// remote-controlled input is the rule in devPlugins/CLAUDE.md, and this is one.
    /// </summary>
    private const int MaxFilesPerSync = 200;

    /// <summary>Stop after this many consecutive download failures rather than hammering a broken host.</summary>
    private const int MaxConsecutiveFailures = 5;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly OfficialCatalog _catalog;
    private readonly Configuration   _config;

    private volatile bool _running;
    public bool IsRunning => _running;

    public string LastStatus { get; private set; } = string.Empty;

    public ChallengeSyncService(OfficialCatalog catalog, Configuration config)
    {
        _catalog = catalog;
        _config  = config;
    }

    public async Task<SyncResult> SyncAsync()
    {
        if (_running)
            return new SyncResult { Ok = false, Message = "A sync is already running." };

        _running = true;
        try
        {
            var result = await RunAsync().ConfigureAwait(false);
            LastStatus = result.Message;
            return result;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Sync] failed");
            LastStatus = "Sync failed. Check your connection.";
            return new SyncResult { Ok = false, Message = LastStatus };
        }
        finally
        {
            _running = false;
        }
    }


    private async Task<SyncResult> RunAsync()
    {
        // 1. Master list.
        string masterJson;
        try
        {
            masterJson = await Http.GetStringAsync(Bust(MasterPath)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Sync] could not fetch the master list: {ex.Message}");
            return new SyncResult { Ok = false, Message = "Couldn't reach the challenge repository." };
        }

        MasterList? master;
        try { master = JsonConvert.DeserializeObject<MasterList>(masterJson); }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Sync] master list is not valid JSON");
            return new SyncResult { Ok = false, Message = "The challenge list is malformed. Try again later." };
        }

        if (master?.Challenges == null)
            return new SyncResult { Ok = false, Message = "The challenge list is empty or malformed." };

        if (master.SchemaVersion > 1)
        {
            // Forward compatibility: refuse rather than misread a newer format.
            return new SyncResult
            {
                Ok = false,
                Message = $"This challenge list needs a newer plugin version (schema {master.SchemaVersion}).",
            };
        }

        _catalog.EnsureDirectory();

        int added = 0, updated = 0, rejected = 0, consecutiveFailures = 0, processed = 0;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 2. Each challenge file the master vouches for.
        foreach (var entry in master.Challenges)
        {
            if (string.IsNullOrWhiteSpace(entry.Id)) continue;

            if (++processed > MaxFilesPerSync)
            {
                Plugin.Log.Warning($"[Sync] stopped at the {MaxFilesPerSync}-file cap; " +
                                   $"{master.Challenges.Count - processed + 1} not fetched.");
                break;
            }

            if (consecutiveFailures >= MaxConsecutiveFailures)
            {
                Plugin.Log.Warning("[Sync] too many consecutive failures; stopping early.");
                break;
            }

            keep.Add(entry.Id);

            // Skip anything already cached with the expected hash.
            string localPath = _catalog.PathFor(entry.Id);
            if (System.IO.File.Exists(localPath) && !string.IsNullOrWhiteSpace(entry.Sha256))
            {
                string localHash = OfficialCatalog.Sha256Hex(System.IO.File.ReadAllBytes(localPath));
                if (string.Equals(localHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    consecutiveFailures = 0;
                    continue;
                }
            }

            // Same cache-busting: a challenge file edited in place would otherwise be served
            // stale, and its hash would then mismatch the master list and get rejected.
            string url = Bust(string.IsNullOrWhiteSpace(entry.File)
                ? $"challenges/{entry.Id}.json"
                : entry.File.TrimStart('/'));

            string body;
            try
            {
                body = await Http.GetStringAsync(url).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                Plugin.Log.Warning($"[Sync] {entry.Id}: download failed ({ex.Message}).");
                continue;
            }

            // 3. Verify against the master list's hash before trusting a single byte of it.
            byte[] bytes = new UTF8Encoding(false).GetBytes(body);
            if (!string.IsNullOrWhiteSpace(entry.Sha256))
            {
                string actual = OfficialCatalog.Sha256Hex(bytes);
                if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log.Warning($"[Sync] {entry.Id}: hash mismatch — rejected.");
                    rejected++;
                    continue;
                }
            }

            // Must parse as a challenge, and must be well-formed, or it is not worth storing.
            CustomChallenge? parsed;
            try { parsed = JsonConvert.DeserializeObject<CustomChallenge>(body); }
            catch { parsed = null; }

            if (parsed == null || string.IsNullOrWhiteSpace(parsed.Title))
            {
                Plugin.Log.Warning($"[Sync] {entry.Id}: unusable payload — rejected.");
                rejected++;
                continue;
            }

            bool existed = System.IO.File.Exists(localPath);
            _catalog.WriteChallenge(entry.Id, body);
            if (existed) updated++; else added++;
        }

        // 4. Store the master list last, so a failed sync leaves the previous good one in place.
        _catalog.WriteMaster(masterJson);

        int removed = _catalog.PruneOrphans(keep);
        _catalog.Load();

        _config.LastSyncUtc = DateTime.UtcNow;
        _config.StateVersion++;

        string msg = $"Synced: {added} new, {updated} updated"
                   + (removed  > 0 ? $", {removed} removed"   : string.Empty)
                   + (rejected > 0 ? $", {rejected} rejected" : string.Empty)
                   + $". {_catalog.Count} official challenge(s) available.";

        // GitHub's raw CDN holds files for up to five minutes, so a challenge published moments
        // ago genuinely will not be here yet. Say so — a silent "0 available" reads as a fault.
        if (_catalog.Count == 0)
            msg += " If something was just published, it can take up to 5 minutes to appear.";

        Plugin.Log.Information("[Sync] " + msg);
        return new SyncResult
        {
            Ok = true, Message = msg,
            Added = added, Updated = updated, Removed = removed, Rejected = rejected,
        };
    }
}
