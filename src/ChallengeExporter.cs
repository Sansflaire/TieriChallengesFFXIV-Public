#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Newtonsoft.Json;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. Turns locally authored challenges into the files the public repo
/// serves: one <c>challenges/&lt;guid&gt;.json</c> per challenge plus a <c>master.json</c> index.
///
/// <para>This is the "create in dev → commit → push → build" pipeline. Export writes into a
/// checkout of the public repo; committing and pushing publishes them, and every user's next
/// sync picks them up.</para>
///
/// <para><b>GUIDs are carried through unchanged.</b> That is the whole point — the GUID a
/// challenge is authored with is the GUID every player receives, so completion means the same
/// thing on every install. Exporting never regenerates an id.</para>
/// </summary>
internal static class ChallengeExporter
{
    /// <summary>
    /// Default location of the SYNC repo checkout, used to prefill the export path.
    /// </summary>
    /// <remarks>
    /// This pointed at a <c>TieriChallengesFFXIV-Public</c> clone until 2026-08-25, left over from
    /// before the three-way repo split. Challenge data moved to <c>-Sync</c> in that split and
    /// <see cref="ChallengeSyncService"/> fetches from there, so every publish after it wrote to a
    /// repo nothing reads — the files landed, the commit pushed, the release looked fine, and no
    /// player ever saw the change. See <see cref="WrongRepo"/>, which now refuses that outright.
    /// </remarks>
    public const string DefaultRepoPath =
        @"C:\Users\trist\AppData\Roaming\XIVLauncher\devPlugins\TieriChallengesFFXIV\syncrepo";

    /// <summary>Owner/name of the repo the plugin actually syncs challenges from.</summary>
    private const string SyncRepoName = "TieriChallengesFFXIV-Sync";

    /// <summary>
    /// Reject an export target that is not a checkout of the sync repo, returning the reason.
    /// Null means the path is fine.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is a refusal and not a warning.</b> Publishing to the wrong repo fails
    /// completely silently: every file writes, the hashes are correct, the commit and push
    /// succeed, and the only symptom is that players never receive the change. It cost a
    /// publish-and-debug cycle to notice that difficulty ratings were missing in game purely
    /// because the data had gone to <c>-Public</c> instead of <c>-Sync</c>.</para>
    ///
    /// <para>Deliberately mirrors <c>BanAdmin.PointsAtPublicRepo</c>, which guards the opposite
    /// direction for the ban ledger. Both exist because the working folder's own <c>origin</c> is
    /// the public repo, so a hand-typed path that looks right is frequently the wrong one. A path
    /// with no <c>.git</c> at all is allowed through — that is a plain folder, not a mis-aimed
    /// checkout, and staging an export somewhere neutral is legitimate.</para>
    /// </remarks>
    private static string? WrongRepo(string repoRoot)
    {
        try
        {
            string cfg = Path.Combine(repoRoot, ".git", "config");
            if (!File.Exists(cfg)) return null;

            string text = File.ReadAllText(cfg);
            if (text.Contains(SyncRepoName, StringComparison.OrdinalIgnoreCase)) return null;

            return $"REFUSED — that folder is a git checkout, but not of {SyncRepoName}. "
                 + "Challenges published anywhere else are never fetched by the plugin: the files "
                 + "would write and push successfully and no player would ever see them. "
                 + $"Point the path at a {SyncRepoName} clone.";
        }
        catch
        {
            // Unreadable .git/config is not proof of anything; let the export proceed.
            return null;
        }
    }

    public sealed class ExportReport
    {
        public bool   Ok      { get; init; }
        public string Message { get; init; } = string.Empty;
        public int    Written { get; init; }
        public int    Skipped { get; init; }

        /// <summary>
        /// Challenges the previous master list published that this export does NOT include.
        /// Publishing over them removes them from every user's catalogue, so they are surfaced
        /// rather than dropped silently. Empty on a normal publish.
        /// </summary>
        public List<string> Removed { get; init; } = new();

        /// <summary>Already-published challenges whose content changed — matched by GUID.</summary>
        public int Updated { get; init; }
    }

    /// <summary>
    /// Write every well-formed, fully-detailed challenge into <paramref name="repoRoot"/>.
    ///
    /// Challenges missing a name or description are skipped rather than published — the
    /// description is the line the completion popup shows, so publishing a blank one ships a
    /// broken-looking toast to everyone.
    ///
    /// <para><b>Editing a published challenge works by GUID.</b> The id IS the filename, so
    /// re-exporting an edited challenge overwrites <c>challenges/&lt;guid&gt;.json</c> in place and
    /// records its new SHA-256 in the master list. A user's next sync sees the hash differ from
    /// their cached copy, re-downloads it, and keeps their completion — which is keyed by the same
    /// GUID and never touched.</para>
    /// </summary>
    /// <param name="allowRemovals">
    /// Permit publishing a set that omits challenges the live master list contains. Guard rail,
    /// default off: master.json is regenerated from the outgoing set alone, so a challenge missing
    /// from it — because it was deleted, or because this machine's config never had it — would
    /// vanish for every user with no warning.
    /// </param>
    public static ExportReport Export(IReadOnlyList<CustomChallenge> challenges, string repoRoot,
                                      IReadOnlyList<string>? categories = null,
                                      bool allowRemovals = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(repoRoot))
                return new ExportReport { Ok = false, Message = "No output folder set." };

            if (WrongRepo(repoRoot) is { } wrong)
                return new ExportReport { Ok = false, Message = wrong };

            string dir = Path.Combine(repoRoot, "challenges");
            Directory.CreateDirectory(dir);

            string masterPath = Path.Combine(dir, "master.json");
            var    previous   = ReadPreviousMaster(masterPath);

            var enc     = new UTF8Encoding(false);
            var entries = new List<MasterEntry>();
            var pending = new List<(string Path, string Json)>();
            int written = 0, skipped = 0, updated = 0;

            foreach (var c in challenges)
            {
                if (string.IsNullOrWhiteSpace(c.Id) || !ChallengeCatalog.IsGuid(c.Id)) { skipped++; continue; }
                if (string.IsNullOrWhiteSpace(c.Title) || string.IsNullOrWhiteSpace(c.Detail)) { skipped++; continue; }
                if (!c.IsWellFormed()) { skipped++; continue; }

                // Indented, stable formatting so a content change produces a readable git diff.
                //
                // LINE ENDINGS ARE LOAD-BEARING. Newtonsoft indents with Environment.NewLine, so
                // on Windows this is CRLF — but git normalises to LF on commit, and
                // raw.githubusercontent then serves LF. Hashing the CRLF form made every
                // published challenge fail verification on download with "hash mismatch".
                // Normalise to LF first, and hash exactly what will be served.
                string json = JsonConvert.SerializeObject(c, Formatting.Indented)
                                         .Replace("\r\n", "\n");
                string hash = OfficialCatalog.Sha256Hex(enc.GetBytes(json));

                // Revision is carried, and only bumped when the bytes actually changed. It used to
                // be hardcoded to 1, which made it a field that always lied. Sync compares hashes
                // rather than revisions, so this is for humans reading the repo — but a number
                // that never moves is worse than no number.
                int revision = 1;
                if (previous.TryGetValue(c.Id, out var before))
                {
                    bool changed = !string.Equals(before.Sha256, hash, StringComparison.OrdinalIgnoreCase);
                    revision = changed ? before.Revision + 1 : before.Revision;
                    if (changed) updated++;
                }

                // Nothing is written until the removal check below has passed.
                pending.Add((Path.Combine(dir, c.Id + ".json"), json));

                entries.Add(new MasterEntry
                {
                    Id        = c.Id,
                    File      = $"challenges/{c.Id}.json",
                    Sha256    = hash,
                    Revision  = revision,
                    Title     = c.Title,
                    Category  = c.Category,
                    SortOrder = c.SortOrder,
                });

                written++;
            }

            // Anything the live list has that this export does not. Publishing would delete it for
            // every user, so it stops here unless that was asked for explicitly.
            var outgoing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries) outgoing.Add(e.Id);

            var removed = new List<string>();
            foreach (var kv in previous)
                if (!outgoing.Contains(kv.Key))
                    removed.Add(string.IsNullOrWhiteSpace(kv.Value.Title) ? kv.Key : kv.Value.Title);

            if (removed.Count > 0 && !allowRemovals)
            {
                string names = string.Join(", ", removed);
                string stop  = $"Refusing to publish: {removed.Count} already-published challenge(s) "
                             + $"are missing from this export and would be REMOVED for every user — {names}. "
                             + "Tick \"Allow removals\" if that is intended.";
                Plugin.Log.Warning("[Export] " + stop);
                return new ExportReport { Ok = false, Message = stop, Removed = removed };
            }

            // Past the guard: commit the files to disk.
            foreach (var (path, json) in pending)
                File.WriteAllText(path, json, enc);

            entries.Sort((a, b) => a.SortOrder != b.SortOrder
                ? a.SortOrder.CompareTo(b.SortOrder)
                : string.CompareOrdinal(a.Title, b.Title));

            var master = new MasterList
            {
                // Still 1. The sync service rejects any master list claiming a schema newer than
                // it knows, so bumping this to announce the added Categories field would make
                // every installed plugin throw away the whole catalogue. Adding an optional
                // property is backward compatible on its own — older builds ignore it.
                SchemaVersion = 1,
                Generated     = DateTime.UtcNow.ToString("o"),
                Challenges    = entries,
                Categories    = categories != null ? new List<string>(categories) : new List<string>(),
            };

            // master.json itself is not hash-verified, but keep it LF too so the repo is
            // consistent and diffs stay clean.
            File.WriteAllText(masterPath,
                              JsonConvert.SerializeObject(master, Formatting.Indented)
                                         .Replace("\r\n", "\n"), enc);

            // Stop git from ever converting these files. Without this, a checkout on a machine
            // with core.autocrlf=true rewrites them to CRLF, and the next export/commit cycle
            // churns every file even when nothing changed.
            string attrs = Path.Combine(repoRoot, ".gitattributes");
            if (!File.Exists(attrs))
                File.WriteAllText(attrs, "challenges/*.json -text\n", enc);

            string msg = $"Exported {written} challenge(s)"
                       + (updated > 0 ? $", {updated} updated in place by GUID" : string.Empty)
                       + (skipped > 0 ? $", skipped {skipped} (missing name/description or not well-formed)" : string.Empty)
                       + (removed.Count > 0 ? $", REMOVED {removed.Count}" : string.Empty)
                       + $" to {dir}";
            Plugin.Log.Information("[Export] " + msg);

            return new ExportReport
            {
                Ok = true, Message = msg, Written = written, Skipped = skipped,
                Updated = updated, Removed = removed,
            };
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Export] failed");
            return new ExportReport { Ok = false, Message = $"Export failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// The master list already in the checkout, keyed by GUID. This is what makes an export an
    /// UPDATE rather than a fresh publish: it supplies the previous hash and revision per id, and
    /// it is the only way to notice that something published is about to disappear.
    ///
    /// <para>Unreadable or absent returns empty, which is correct for a first publish. It does
    /// mean a corrupt master.json disables the removal guard — logged loudly for that reason.</para>
    /// </summary>
    private static Dictionary<string, MasterEntry> ReadPreviousMaster(string masterPath)
    {
        var map = new Dictionary<string, MasterEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(masterPath)) return map;

            var parsed = JsonConvert.DeserializeObject<MasterList>(File.ReadAllText(masterPath));
            if (parsed?.Challenges == null)
            {
                Plugin.Log.Warning("[Export] existing master.json is unreadable — removal guard is OFF for this publish.");
                return map;
            }

            foreach (var e in parsed.Challenges)
                if (!string.IsNullOrWhiteSpace(e.Id)) map[e.Id] = e;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Export] could not read the existing master.json ({ex.Message}) — removal guard is OFF.");
        }
        return map;
    }
}
#endif
