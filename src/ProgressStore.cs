using System;
using System.Collections.Generic;
using System.IO;

using Newtonsoft.Json;

namespace TieriChallengesFFXIV;

/// <summary>
/// Progress that is NOT completion: how far through a multi-objective challenge the player is.
///
/// <para><b>Why this exists.</b> <see cref="ChallengeTracker"/>'s <c>_visited</c> and
/// <c>_sequence</c> are deliberately session-scoped — "visit these four spots within one login
/// session" is a real constraint and losing the partial progress on logout is the point of it.
/// Adventures and quest chains are the opposite: the player is explicitly told they have as long
/// as they like, so their progress has to outlive the session. Same data, opposite lifetime, so it
/// gets its own home rather than a flag on the old one.</para>
///
/// <para><b>Reset wipes this.</b> Unlike the permanent completion ledger and the race best-time
/// file, partial progress is exactly what "let me do these again" means — a half-finished chain
/// surviving a reset would leave the player unable to start it over.</para>
///
/// <para>Written atomically and read non-fatally, like the completion stores.</para>
/// </summary>
public sealed class ProgressStore
{
    private const string FileName = "progress-current.json";

    /// <summary>The on-disk shape. One object so the two maps stay in a single atomic write.</summary>
    private sealed class Data
    {
        /// <summary>Chain GUID → index of the step currently being worked on.</summary>
        public Dictionary<string, int> ChainStep { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Challenge GUID → which stop indices are already satisfied.</summary>
        public Dictionary<string, List<int>> Stops { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly string _path;
    private Data _data = new();

    public ProgressStore(string configDirectory)
    {
        _path = Path.Combine(configDirectory, FileName);
    }

    public string Path_ => _path;

    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            string json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json)) return;

            var parsed = JsonConvert.DeserializeObject<Data>(json);
            if (parsed == null) return;

            // Rebuilt with the case-insensitive comparers — a GUID's casing must never split a key.
            var d = new Data();
            foreach (var kv in parsed.ChainStep ?? new()) d.ChainStep[kv.Key] = kv.Value;
            foreach (var kv in parsed.Stops     ?? new()) d.Stops[kv.Key]     = kv.Value ?? new List<int>();
            _data = d;

            Diag.Info(
                $"[Progress] loaded {_data.ChainStep.Count} chain position(s), "
              + $"{_data.Stops.Count} partial objective set(s).");
        }
        catch (Exception ex)
        {
            // Non-fatal: losing partial progress is bad, taking the plugin down with it is worse.
            Diag.Error(ex, $"Failed to read {_path}");
        }
    }

    public void Save()
    {
        try
        {
            string json = JsonConvert.SerializeObject(_data, Formatting.Indented);
            string tmp  = _path + ".tmp";

            File.WriteAllText(tmp, json);

            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else                    File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            Diag.Error(ex, $"Failed to write {_path}");
        }
    }

    // ── Chains ───────────────────────────────────────────────────────────────

    /// <summary>Which step of a chain the player is on. 0 = the first, nothing done yet.</summary>
    public int ChainStep(string guid) =>
        !string.IsNullOrEmpty(guid) && _data.ChainStep.TryGetValue(guid, out var i) ? i : 0;

    public void SetChainStep(string guid, int index)
    {
        if (string.IsNullOrEmpty(guid)) return;
        _data.ChainStep[guid] = Math.Max(0, index);
        Save();
    }

    // ── Partial objectives ───────────────────────────────────────────────────

    /// <summary>Stop indices already satisfied for this challenge. Never null.</summary>
    public HashSet<int> Stops(string guid)
    {
        if (!string.IsNullOrEmpty(guid) && _data.Stops.TryGetValue(guid, out var list) && list != null)
            return new HashSet<int>(list);
        return new HashSet<int>();
    }

    /// <summary>
    /// How many stops are recorded, without materialising the set.
    ///
    /// <para>Separate from <see cref="Stops"/> because the challenge row asks this question once
    /// per row per frame, and <see cref="Stops"/> allocates a <see cref="HashSet{T}"/> copy every
    /// time it is called — deliberately, so a caller cannot mutate the stored list through it.</para>
    /// </summary>
    public int StopCount(string guid) =>
        !string.IsNullOrEmpty(guid) && _data.Stops.TryGetValue(guid, out var list) && list != null
            ? list.Count : 0;

    public void SetStops(string guid, HashSet<int> stops)
    {
        if (string.IsNullOrEmpty(guid)) return;

        var list = new List<int>(stops);
        list.Sort();
        _data.Stops[guid] = list;
        Save();
    }

    /// <summary>
    /// Drop everything recorded for one challenge. Called when it completes — a finished
    /// challenge's partial progress is dead weight, and leaving it would let a later Reset restore
    /// a half-done state for something the ledger says is finished.
    /// </summary>
    public void Clear(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return;

        bool changed = _data.ChainStep.Remove(guid);
        changed |= _data.Stops.Remove(guid);
        if (changed) Save();
    }

    /// <summary>Wipes all partial progress. Part of the Reset path.</summary>
    public void ResetAll()
    {
        _data = new Data();
        Save();
        Diag.Info("[Progress] all partial progress cleared.");
    }

    public int TrackedCount => _data.ChainStep.Count + _data.Stops.Count;
}
