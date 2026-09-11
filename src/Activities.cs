#if DEV_BUILD
using System;
using System.Collections.Generic;

using Dalamud.Game.ClientState.Conditions;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. One thing a player can engage with <b>infinitely</b> — the defining
/// property of this whole tab.
///
/// <para><b>An activity is NOT a challenge, and the distinction is the reason it needs its own tab
/// rather than a new <c>ChallengeKind</c>.</b> A challenge is finished once and stays finished; its
/// identity is a GUID, its completion is a date, and the entire completion-store contract is built
/// on write-once. An activity has no end state at all. Running it twice is the point, so there is
/// nothing for <c>MarkComplete</c> to mark and no meaning to "have I done this one".</para>
///
/// <para>What it produces instead is a RUN — see <see cref="ActivityRun"/> — and runs accumulate.
/// Trying to express that as a challenge would have meant either a challenge that completes and then
/// un-completes, or one that never completes and sits at 0% forever. Both are worse than a tab.</para>
/// </summary>
internal sealed class ActivityDef
{
    public string Id    { get; init; } = string.Empty;
    public string Name  { get; init; } = string.Empty;
    public string Blurb { get; init; } = string.Empty;

    /// <summary>The one-line rules summary shown under the name in the detail pane.</summary>
    public string Rules { get; init; } = string.Empty;
}

/// <summary>
/// DEVELOPER BUILD ONLY. Which activities exist, and which maps they are authored for.
/// </summary>
internal static class ActivityCatalog
{
    public const string WildTrailId = "wild-trail";

    public static readonly ActivityDef WildTrail = new()
    {
        Id    = WildTrailId,
        Name  = "Wild Trail",
        Blurb = "A chain of buried clues, each one pointing at the next.",
        Rules = "Read the clue, walk to where you think it means, and dig. A wrong guess costs you "
              + "nothing but time. Leaving the zone ends the run and throws the time away.",
    };

    public static readonly IReadOnlyList<ActivityDef> All = new[] { WildTrail };

    public static ActivityDef? Find(string id)
    {
        foreach (var a in All)
            if (string.Equals(a.Id, id, StringComparison.Ordinal)) return a;

        return null;
    }

    /// <summary>One map an activity can be played on.</summary>
    internal readonly record struct AuthoredMap(uint Territory, string Name);

    /// <summary>
    /// The maps this activity is authored for.
    ///
    /// <para><b>"Authored" means somebody drew the map's bounds by hand, and that is a derived fact
    /// rather than a list.</b> A hand-drawn box is the one input the generator has that is a
    /// MEASUREMENT instead of an inference — every automatic attempt to work out where a player can
    /// stand failed differently, which is the finding the whole placement pipeline now rests on. So
    /// a map with a box is exactly a map whose trails can be trusted, and a hard-coded list of
    /// territory ids would be a second answer to that question, free to disagree with the first.</para>
    ///
    /// <para><b>Consequence worth knowing: there is currently room for exactly ONE.</b>
    /// <see cref="DigTuning"/> stores a single box against a single <c>BoxTerritory</c>, so drawing a
    /// box in a second zone replaces the first. That is a real limit of the tuning store and not of
    /// this method — when the box store grows to a per-territory dictionary, this returns more rows
    /// with no change here.</para>
    /// </summary>
    public static List<AuthoredMap> AuthoredMaps()
    {
        var list = new List<AuthoredMap>();

        if (!DigTuning.BoxSet) return list;

        uint territory = DigTuning.BoxTerritory;
        if (!DigTuning.TryManualBox(territory, out _, out _, out _, out _)) return list;

        list.Add(new AuthoredMap(territory, ZoneIndex.ZoneName(territory)));
        return list;
    }
}

/// <summary>
/// DEVELOPER BUILD ONLY. Owns a live activity run: what was started, on what settings, and every way
/// it can end.
///
/// <para><b>It is a THIN layer over the dig test, on purpose.</b> The trail itself — placement,
/// clues, HUD, radar, digging — is <see cref="DigRoamService"/> and is not duplicated here. What this
/// adds is the three things an activity has and a lab test does not: a chosen difficulty and clue
/// count that do not disturb the lab's own sliders, a recorded result, and a set of conditions that
/// throw the run away.</para>
///
/// <para><b>Cancellation discards rather than saves.</b> Sansflaire's rule: a zone change, a logout or
/// entering an instance cancels the trail and throws out its progress. So the elapsed time of an
/// abandoned run is never written — a number in the history that looks like a result and was not
/// earned is worse than no number.</para>
/// </summary>
internal sealed class ActivityService
{
    private readonly DigTests _tests;

    private bool   _running;
    private string _activityId = string.Empty;
    private string _mapName    = string.Empty;
    private uint   _territory;
    private int    _difficulty;
    private int    _clues;

    /// <summary>Why the last run ended, for the pane to show without the player watching chat.</summary>
    public string LastOutcome { get; private set; } = string.Empty;

    public bool   Running    => _running;
    public string ActivityId => _activityId;
    public uint   Territory  => _territory;

    public ActivityService(DigTests tests)
    {
        _tests = tests;

        _tests.Roam.Finished  += OnFinished;
        _tests.Roam.Abandoned += OnAbandoned;
    }

    public void Dispose()
    {
        _tests.Roam.Finished  -= OnFinished;
        _tests.Roam.Abandoned -= OnAbandoned;
    }

    /// <summary>
    /// The lowest and highest clue counts an activity may ask for. The floor is 1 because a
    /// one-clue trail is a legitimate quick game; the ceiling matches
    /// <see cref="DigRoamService"/>'s own clamp so the control cannot promise a number the placement
    /// will silently reduce.
    /// </summary>
    public const int MinClues = 1;
    public const int MaxClues = 20;

    /// <summary>
    /// Starts an activity, or explains why not.
    ///
    /// <para><b>Difficulty arrives 0..10 and is converted here.</b> The generator works in 0..1 and
    /// the player's control is whole numbers, so exactly one place divides — doing it at the control
    /// would put a float in the config and doing it in the generator would put a 0..10 scale in the
    /// clue writer. Neither belongs there.</para>
    /// </summary>
    public string Start(ActivityDef activity, uint territory, string mapName, int difficulty, int clues)
    {
        if (_running) return "an activity is already running — finish or abandon it first.";

        if (Plugin.ClientState.TerritoryType != territory)
            return $"this activity is authored for {mapName}. Travel there and start it from inside.";

        if (CancelReason() is { } blocked)
            return $"cannot start right now — {blocked}.";

        difficulty = Math.Clamp(difficulty, 0, 10);
        clues      = Math.Clamp(clues, MinClues, MaxClues);

        // NOTHING IS APPLIED HERE, AND THAT IS THE POINT. The activity reads the live DigTuning
        // values — the lab's own — so HUD placement, dig speed, the multiplier and every colour are
        // whatever is currently set, with no copy in between that could disagree. The pushed
        // baseline is a save slot for those values, not a second source of them.
        // DID IT ACTUALLY START? Asked with the start COUNTER, not with IsActive.
        //
        // IsActive is "phase is not Off", and a trail that has just been finished sits in Done for
        // fifteen seconds before it clears. So starting an activity during that window and having
        // placement refuse would have read as a successful start — the run would be marked live with
        // nothing buried, and the next zone change would announce a cancellation for a trail that
        // never existed. StartCount is incremented only past every refusal in Start, which makes it
        // the one unambiguous signal.
        //
        // The refusals themselves are prose, so parsing the returned sentence is not an option
        // either: there are five of them and a sixth would silently not be recognised.
        int before = _tests.Roam.StartCount;

        string result = _tests.StartRoamActivity(clues, difficulty / 10f);

        if (_tests.Roam.StartCount == before) return result;

        _running    = true;
        _activityId = activity.Id;
        _mapName    = mapName;
        _territory  = territory;
        _difficulty = difficulty;
        _clues      = clues;

        LastOutcome = string.Empty;
        return result;
    }

    public string Abandon()
    {
        if (!_running) return "no activity is running.";
        return _tests.Roam.Stop();
    }

    /// <summary>
    /// Watches for every way a run ends that is not finishing it.
    ///
    /// <para><b>Polled from the framework tick, because none of these raise an event we can take.</b>
    /// A territory change, a logout and a duty boundary are all states rather than notifications, and
    /// the cheapest honest way to watch a state is to look at it. The whole check is three flag
    /// reads and an integer compare, gated behind "is anything running at all".</para>
    /// </summary>
    public void Tick()
    {
        if (!_running) return;

        if (CancelReason() is not { } reason) return;

        // Stop the trail FIRST. OnAbandoned writes LastOutcome, and letting it run with the generic
        // wording would overwrite the specific reason we know here with "abandoned".
        _tests.Roam.Stop();

        _running    = false;
        LastOutcome = $"Run cancelled — {reason}. Progress discarded.";

        Plugin.ChatGui.Print($"[Challenges] Activity cancelled — {reason}. The run does not count.");
    }

    /// <summary>
    /// The reason a run cannot continue, or null.
    ///
    /// <para>Order matters only for the message: a player who logs out is also, briefly, between
    /// areas, and "you logged out" is the useful half of that.</para>
    /// </summary>
    private string? CancelReason()
    {
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
            return "you logged out";

        if (_running && Plugin.ClientState.TerritoryType != _territory)
            return "you left the zone";

        try
        {
            if (Plugin.Condition[ConditionFlag.BetweenAreas] ||
                Plugin.Condition[ConditionFlag.BetweenAreas51])
                return "you are changing areas";

            // BoundByDuty covers instanced content of every kind — dungeons, trials, raids, deep
            // dungeons. Checked as three flags rather than one because the game sets different ones
            // for different content, and a duty that only sets 56 would otherwise slip through.
            if (Plugin.Condition[ConditionFlag.BoundByDuty] ||
                Plugin.Condition[ConditionFlag.BoundByDuty56] ||
                Plugin.Condition[ConditionFlag.BoundByDuty95])
                return "you entered an instance";
        }
        catch (Exception ex)
        {
            // A condition read that throws is not a reason to kill the player's run.
            Diag.Error($"[Activity] condition read failed: {ex.Message}");
        }

        return null;
    }

    private void OnFinished(double seconds, int digs)
    {
        if (!_running) return;

        var run = new ActivityRun
        {
            ActivityId = _activityId,
            MapName    = _mapName,
            Territory  = _territory,
            Difficulty = _difficulty,
            Clues      = _clues,
            Digs       = digs,
            Seconds    = seconds,
            WhenUtc    = DateTime.UtcNow,
        };

        var best = ActivityRuns.Best(_activityId, _territory);
        bool isBest = best == null || seconds < best.Seconds;

        ActivityRuns.Record(run);

        _running    = false;
        LastOutcome = isBest
            ? $"New best — {CompletionStore.FormatRaceTime(seconds)}!"
            : $"Finished in {CompletionStore.FormatRaceTime(seconds)}.";
    }

    private void OnAbandoned()
    {
        if (!_running) return;

        _running    = false;
        LastOutcome = "Run abandoned. Progress discarded.";
    }
}
#endif
