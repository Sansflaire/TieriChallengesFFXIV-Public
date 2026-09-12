using System;
using System.Collections.Generic;
using System.IO;

using Newtonsoft.Json;

namespace TieriChallengesFFXIV;

/// <summary>
/// One finished activity run, and the whole history of them.
///
/// <para><b>This is a THIRD store with a fourth lifetime, and it is deliberately not merged into any
/// of the existing three.</b> <c>completions-permanent.json</c> is write-once per challenge GUID,
/// <c>race-times.json</c> keeps one personal best per race, and <c>progress-current.json</c> is wiped
/// by Reset. An activity has none of those shapes: it is repeatable without limit, every run is worth
/// keeping rather than only the best one, and an activity has no GUID to key on because it is not a
/// challenge. So it gets its own file.</para>
///
/// <para><b>Unfinished runs are never written.</b> A trail abandoned by a zone change is not a slow
/// run, it is not a run — recording it with its elapsed time would put a number in the list that
/// looks like a result and is not one.</para>
/// </summary>
internal sealed class ActivityRun
{
    public string   ActivityId { get; set; } = string.Empty;
    public string   MapName    { get; set; } = string.Empty;
    public uint     Territory  { get; set; }
    public int      Difficulty { get; set; }
    public int      Clues      { get; set; }
    public int      Digs       { get; set; }
    public double   Seconds    { get; set; }
    public DateTime WhenUtc    { get; set; }

    /// <summary>
    /// The line a player copies to send to somebody else.
    ///
    /// <para><b>One sentence carrying all four facts Sansflaire asked for</b> — map, difficulty, clue count
    /// and time — with no plugin jargon and no ids. It has to make sense to a friend who has never
    /// opened this window, so it names the activity and the place in words rather than assuming the
    /// reader already knows which "trail" is meant.</para>
    ///
    /// <para>The time is formatted by <see cref="CompletionStore.FormatRaceTime"/>, the same function
    /// races use, so two timed things in one plugin never print a duration two different ways.</para>
    /// </summary>
    public string ShareLine()
    {
        string time = CompletionStore.FormatRaceTime(Seconds);
        string clue = Clues == 1 ? "1 clue" : $"{Clues} clues";

        return $"I followed a Wild Trail in {MapName} — {clue} at difficulty {Difficulty}/10, "
             + $"dug up in {time}.";
    }
}

/// <summary>
/// The saved history of finished activity runs.
///
/// <para>Newest first, capped, and written atomically like every other store here. A failed read is
/// non-fatal and yields an empty history: a corrupt leaderboard must never stop the activity being
/// playable, because the runs are the record of the fun and not the fun itself.</para>
/// </summary>
internal static class ActivityRuns
{
    /// <summary>
    /// How many runs are kept. <b>A cap rather than unbounded growth</b>, because this file is
    /// rewritten in full on every finish and an unbounded list makes that cost grow without limit
    /// for a list nobody scrolls past the first screen of.
    /// </summary>
    private const int MaxRuns = 200;

    private static List<ActivityRun>? _runs;

    private static string PathOf() =>
        Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "activity-runs.json");

    /// <summary>Every finished run, newest first. Loaded once and kept.</summary>
    public static IReadOnlyList<ActivityRun> All()
    {
        if (_runs != null) return _runs;

        _runs = new List<ActivityRun>();

        try
        {
            string path = PathOf();
            if (!File.Exists(path)) return _runs;

            var read = JsonConvert.DeserializeObject<List<ActivityRun>>(File.ReadAllText(path));
            if (read != null) _runs = read;
        }
        catch (Exception ex)
        {
            Diag.Error($"[Activity] run history read failed: {ex.Message}");
            _runs = new List<ActivityRun>();
        }

        return _runs;
    }

    /// <summary>Runs of one activity on one map, newest first.</summary>
    public static List<ActivityRun> For(string activityId, uint territory)
    {
        var list = new List<ActivityRun>();

        foreach (var r in All())
        {
            if (!string.Equals(r.ActivityId, activityId, StringComparison.Ordinal)) continue;
            if (r.Territory != territory) continue;
            list.Add(r);
        }

        return list;
    }

    /// <summary>The fastest finished run of this activity on this map, or null.</summary>
    public static ActivityRun? Best(string activityId, uint territory)
    {
        ActivityRun? best = null;

        foreach (var r in For(activityId, territory))
            if (best == null || r.Seconds < best.Seconds) best = r;

        return best;
    }

    public static void Record(ActivityRun run)
    {
        var list = (List<ActivityRun>)All();

        list.Insert(0, run);
        if (list.Count > MaxRuns) list.RemoveRange(MaxRuns, list.Count - MaxRuns);

        Save();
    }

    private static void Save()
    {
        try
        {
            string path = PathOf();
            string tmp  = path + ".tmp";

            File.WriteAllText(tmp, JsonConvert.SerializeObject(_runs, Formatting.Indented));

            if (File.Exists(path)) File.Replace(tmp, path, null);
            else                   File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            Diag.Error($"[Activity] run history write failed: {ex.Message}");
        }
    }
}
