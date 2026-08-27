using System;
using System.Collections.Generic;
using System.IO;

using Newtonsoft.Json;

namespace TieriChallengesFFXIV;

/// <summary>
/// Completion state, keyed by challenge GUID, persisted to two files.
///
/// <para><b>Why two files.</b> "Current" is the live truth the UI reads and Reset wipes.
/// "Permanent" is an append-only ledger that Reset never touches, so a wipe is always
/// recoverable. A challenge's first completion date is written to permanent once and then
/// treated as immutable — re-completing after a reset must never overwrite the original date,
/// because the whole point of the ledger is that it remembers when you ACTUALLY first did it.</para>
///
/// <para><b>Why GUIDs.</b> Everything here is keyed by a stable GUID that never changes for the
/// life of a challenge. Renaming a challenge, renumbering it, moving it between categories, or
/// shipping a plugin update that reorders the catalogue all leave completion untouched. The
/// display number is presentation only.</para>
///
/// <para>Both files live in the plugin's own config directory, are written atomically
/// (temp file then replace) so a crash mid-write cannot truncate them, and a failed read is
/// non-fatal — a corrupt current file falls back to whatever permanent still holds.</para>
/// </summary>
public sealed class CompletionStore
{
    private const string CurrentFileName   = "completions-current.json";
    private const string PermanentFileName = "completions-permanent.json";
    private const string RaceTimeFileName  = "race-times.json";

    private readonly string _dir;
    private readonly string _currentPath;
    private readonly string _permanentPath;
    private readonly string _raceTimePath;

    /// <summary>
    /// GUID → best (lowest) time in seconds for a <see cref="ChallengeKind.RaceTimer"/> challenge.
    ///
    /// <para><b>Reset does not touch this, by the same reasoning as the permanent ledger.</b>
    /// A personal best is a record of something that happened, not progress toward something.
    /// Wiping progress means "let me do these again", which is precisely when a player would most
    /// want the time to beat still on screen.</para>
    /// </summary>
    private Dictionary<string, double> _raceTimes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>GUID → when it was completed, this run of the data. Reset clears this.</summary>
    private Dictionary<string, DateTime> _current = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>GUID → EARLIEST known completion. Append-only; never cleared, never overwritten.</summary>
    private Dictionary<string, DateTime> _permanent = new(StringComparer.OrdinalIgnoreCase);

    public CompletionStore(string configDirectory)
    {
        _dir           = configDirectory;
        _currentPath   = Path.Combine(_dir, CurrentFileName);
        _permanentPath = Path.Combine(_dir, PermanentFileName);
        _raceTimePath  = Path.Combine(_dir, RaceTimeFileName);
    }

    public int CurrentCount   => _current.Count;
    public int PermanentCount => _permanent.Count;

    public string CurrentPath   => _currentPath;
    public string PermanentPath => _permanentPath;

    // ── Load / save ──────────────────────────────────────────────────────────

    public void Load()
    {
        try { Directory.CreateDirectory(_dir); }
        catch (Exception ex) { Diag.Error(ex, "Could not create config directory"); }

        _permanent = ReadFile(_permanentPath) ?? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        _current   = ReadFile(_currentPath)   ?? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        _raceTimes = ReadTimes(_raceTimePath) ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        Diag.Info(
            $"[Completions] loaded {_current.Count} current, {_permanent.Count} permanent, "
          + $"{_raceTimes.Count} race time(s).");
    }

    private static Dictionary<string, double>? ReadTimes(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var parsed = JsonConvert.DeserializeObject<Dictionary<string, double>>(json);
            if (parsed == null) return null;

            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in parsed) result[kv.Key] = kv.Value;
            return result;
        }
        catch (Exception ex)
        {
            Diag.Error(ex, $"Failed to read {path}");
            return null;
        }
    }

    private static void WriteTimes(string path, Dictionary<string, double> data)
    {
        try
        {
            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            string tmp  = path + ".tmp";

            File.WriteAllText(tmp, json);

            if (File.Exists(path)) File.Replace(tmp, path, null);
            else                   File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            Diag.Error(ex, $"Failed to write {path}");
        }
    }

    private static Dictionary<string, DateTime>? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var parsed = JsonConvert.DeserializeObject<Dictionary<string, DateTime>>(json);
            if (parsed == null) return null;

            // Rebuild with the case-insensitive comparer — GUID casing must never split a key.
            var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in parsed) result[kv.Key] = kv.Value;
            return result;
        }
        catch (Exception ex)
        {
            // Non-fatal on purpose: a damaged current file must not take the plugin down, and
            // permanent can usually repopulate it.
            Diag.Error(ex, $"Failed to read {path}");
            return null;
        }
    }

    /// <summary>
    /// Atomic write: serialise to a temp file in the same directory, then replace. A crash
    /// mid-write leaves the previous good file intact rather than a truncated one.
    /// </summary>
    private static void WriteFile(string path, Dictionary<string, DateTime> data)
    {
        try
        {
            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            string tmp  = path + ".tmp";

            File.WriteAllText(tmp, json);

            if (File.Exists(path)) File.Replace(tmp, path, null);
            else                   File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            Diag.Error(ex, $"Failed to write {path}");
        }
    }

    public void SaveCurrent()   => WriteFile(_currentPath,   _current);
    public void SavePermanent() => WriteFile(_permanentPath, _permanent);

    public void SaveBoth()
    {
        SaveCurrent();
        SavePermanent();
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    public bool IsComplete(string guid) =>
        !string.IsNullOrEmpty(guid) && _current.ContainsKey(guid);

    /// <summary>When the challenge was completed in the current data, or null.</summary>
    public DateTime? CompletedAt(string guid) =>
        !string.IsNullOrEmpty(guid) && _current.TryGetValue(guid, out var t) ? t : null;

    /// <summary>The earliest completion ever recorded, even if current has been wiped since.</summary>
    public DateTime? FirstEverCompletedAt(string guid) =>
        !string.IsNullOrEmpty(guid) && _permanent.TryGetValue(guid, out var t) ? t : null;

    /// <summary>Formatted for display: "Aug 22, 2026 at 11:04 PM". Stored UTC, shown local.</summary>
    public static string FormatDate(DateTime utc) =>
        utc.ToLocalTime().ToString("MMM d, yyyy 'at' h:mm tt");

    /// <summary>
    /// Just the clock time, for labels too small for <see cref="FormatDate"/> — "last synced
    /// 14:32". Lives here so the UTC→local conversion happens in one place; a caller formatting
    /// the raw stamp itself would print the wrong time for everyone outside UTC.
    /// </summary>
    public static string FormatTimeOfDay(DateTime utc) =>
        utc.ToLocalTime().ToString("HH:mm");

    // ── Race times ───────────────────────────────────────────────────────────

    /// <summary>Best recorded time in seconds for a race, or null if it has never been finished.</summary>
    public double? BestRaceTime(string guid) =>
        !string.IsNullOrEmpty(guid) && _raceTimes.TryGetValue(guid, out var t) ? t : null;

    /// <summary>
    /// Record a finishing time, keeping only the best. Returns true when this run beat the stored
    /// time (or was the first), which is what the UI uses to say "new best" rather than "finished".
    /// </summary>
    public bool RecordRaceTime(string guid, double seconds)
    {
        if (string.IsNullOrEmpty(guid) || seconds <= 0) return false;

        if (_raceTimes.TryGetValue(guid, out var best) && best <= seconds) return false;

        _raceTimes[guid] = seconds;
        WriteTimes(_raceTimePath, _raceTimes);
        return true;
    }

    /// <summary>
    /// "1:23.45" for anything over a minute, "23.45s" below it. Two decimals throughout: a race
    /// decided by a tenth is exactly the race a player wants the tenths for.
    /// </summary>
    public static string FormatRaceTime(double seconds)
    {
        if (seconds < 0) seconds = 0;

        // Rounded BEFORE the split, not by the format string after it. Formatting the remainder
        // rounds it in isolation with nothing to carry into the minutes, so 119.999 s split first
        // and printed as "{1}:{59.999:00.00}" reads "1:60.00", and 59.999 s reads "60.00s" instead
        // of "1:00.00". Rounding to the displayed precision up front makes the carry happen.
        double total = Math.Round(seconds, 2, MidpointRounding.AwayFromZero);

        int    minutes = (int)(total / 60);
        double rest    = total - minutes * 60;

        return minutes > 0
            ? $"{minutes}:{rest:00.00}"
            : $"{rest:0.00}s";
    }

    // ── Mutations ────────────────────────────────────────────────────────────

    /// <summary>
    /// Record a completion. Always writes to current; writes to permanent ONLY if that GUID is
    /// not already there, so the ledger keeps the earliest date forever.
    /// </summary>
    public void MarkComplete(string guid, DateTime? whenUtc = null)
    {
        if (string.IsNullOrEmpty(guid)) return;

        DateTime when = whenUtc ?? DateTime.UtcNow;

        bool newCurrent = !_current.ContainsKey(guid);
        if (newCurrent) _current[guid] = when;

        bool newPermanent = !_permanent.ContainsKey(guid);
        if (newPermanent) _permanent[guid] = when;

        if (newCurrent)   SaveCurrent();
        if (newPermanent) SavePermanent();
    }

    /// <summary>Wipes CURRENT only. The permanent ledger is deliberately untouched.</summary>
    public void ResetCurrent()
    {
        _current.Clear();
        SaveCurrent();
        Diag.Info(
            $"[Completions] current wiped; {_permanent.Count} entries still held in permanent storage.");
    }

    /// <summary>
    /// Repopulate current from the permanent ledger, restoring each challenge's EARLIEST
    /// recorded completion date. Returns how many entries were added.
    /// </summary>
    public int RestoreFromPermanent()
    {
        int added = 0;
        foreach (var kv in _permanent)
        {
            if (_current.ContainsKey(kv.Key)) continue;
            _current[kv.Key] = kv.Value;
            added++;
        }

        if (added > 0) SaveCurrent();
        Diag.Info($"[Completions] restored {added} completion(s) from permanent storage.");
        return added;
    }

    /// <summary>
    /// Rewrite a GUID in both stores, preserving dates. Used by the one-time migration off the
    /// old slug-based ids — a user's existing progress must survive the change.
    /// </summary>
    public bool RemapId(string oldId, string newId)
    {
        bool changed = false;

        if (_current.TryGetValue(oldId, out var cur) && !_current.ContainsKey(newId))
        {
            _current.Remove(oldId);
            _current[newId] = cur;
            changed = true;
        }

        if (_permanent.TryGetValue(oldId, out var perm) && !_permanent.ContainsKey(newId))
        {
            _permanent.Remove(oldId);
            _permanent[newId] = perm;
            changed = true;
        }

        return changed;
    }

    /// <summary>Adopt a legacy completion from the old config dictionary.</summary>
    public void AdoptLegacy(string guid, DateTime whenUtc)
    {
        if (string.IsNullOrEmpty(guid)) return;
        if (!_current.ContainsKey(guid))   _current[guid]   = whenUtc;
        if (!_permanent.ContainsKey(guid)) _permanent[guid] = whenUtc;
    }
}
