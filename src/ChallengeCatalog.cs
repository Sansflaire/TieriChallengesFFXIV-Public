using System;
using System.Collections.Generic;

namespace TieriChallengesFFXIV;

/// <summary>
/// A single challenge definition as the UI sees it. Immutable — completion lives in
/// <see cref="CompletionStore"/>, keyed by <see cref="Id"/>.
/// </summary>
/// <param name="Id">
/// The challenge's permanent GUID. This is the ONLY key used for tracking anywhere in the
/// plugin. It never changes for the life of a challenge — not on rename, not on renumber, not
/// on a plugin update.
/// </param>
/// <param name="Number">
/// <b>A row's position in the list currently on screen, 1-based.</b> Not a property of the
/// challenge — every pane stamps it over the list it is about to draw, so whatever is being shown
/// (a category, a zone, a filtered set, any future view) reads 1, 2, 3 from the top with no gaps.
///
/// <para>The value <see cref="ChallengeCatalog.Combined"/> puts here is only a fallback for
/// non-list callers such as the authoring preview. Any surface that renders a list MUST renumber
/// against its own list — see <c>MainWindow.BuildDetail</c> and <c>FallbackWindow</c>. Notifications
/// show no number at all, because there is no list they are a position in.</para>
///
/// <para>Never persist it or key anything off it; that is what <see cref="Id"/> is for.</para>
/// </param>
/// <param name="Difficulty">
/// 1–5 stars, or 0 for unrated. Authored, not computed — nothing in the plugin can measure how
/// hard a challenge is, so an unrated challenge shows no stars rather than a guessed one.
/// </param>
public sealed record ChallengeDef(
    string        Id,
    string        Category,
    string        Title,
    string        Detail,
    ChallengeKind Kind     = ChallengeKind.Manual,
    bool          IsCustom = false,
    int           Number   = 0,
    ChallengeSource Source = ChallengeSource.Custom,
    string        Hint     = "",
    int           Difficulty = 0,
    ChallengeTheme Theme  = ChallengeTheme.Normal,
    int           StepNumber = 0,
    int           StepTotal  = 0,
    bool          ShowProgress = true)
{
    /// <summary>A quest chain. The Title/Detail/Hint above are the CURRENT step's, not the chain's.</summary>
    public bool IsChain => Theme == ChallengeTheme.Quest && StepTotal > 0;

    /// <summary>
    /// Several objectives worth listing out — a chain's steps, or an adventure's stops. Drives
    /// whether the row offers a way into the objective window.
    /// </summary>
    public bool HasObjectiveList => Theme is ChallengeTheme.Quest or ChallengeTheme.Adventure;

    /// <summary>"Step 2 of 5", or empty when there is nothing to count or the author hid it.</summary>
    public string StepLabel =>
        IsChain && ShowProgress && StepTotal > 1 ? $"Step {StepNumber} of {StepTotal}" : string.Empty;

    /// <summary>A difficulty was authored. Unrated challenges render no star row at all.</summary>
    public bool HasDifficulty => Difficulty is >= 1 and <= 5;

    /// <summary>
    /// The five-slot difficulty meter as text, for the surfaces that cannot draw a bitmap icon —
    /// the plain-ImGui <c>FallbackWindow</c> and the dev-only Challenge Creator.
    /// </summary>
    /// <remarks>
    /// <para><b>Circles, not stars, and deliberately so.</b> These match what the PanacheUI meter
    /// actually renders today: the bundled icon set has no five-point star pair, so
    /// <c>MainWindow.Ico.StarFull/StarEmpty</c> are a filled dot in a ring and a hollow circle.
    /// Using ★/☆ here would make the two surfaces disagree about what the same challenge looks
    /// like, and would promise artwork that does not exist yet. When a real star pair lands,
    /// change the two characters here and the two icon numbers there together.</para>
    ///
    /// <para>Always five slots, never just the earned ones — a proportion is read at a glance,
    /// where a bare count has to be compared against a maximum the row never states. Returns
    /// empty for an unrated challenge, which is the same "no meter at all" the main window shows.</para>
    /// </remarks>
    public string DifficultyMeter() => DifficultyMeterFor(Difficulty);

    /// <inheritdoc cref="DifficultyMeter"/>
    public static string DifficultyMeterFor(int difficulty)
    {
        if (difficulty is < 1 or > 5) return string.Empty;

        Span<char> pips = stackalloc char[5];
        for (int i = 0; i < 5; i++) pips[i] = i < difficulty ? '●' : '○';   // ● / ○
        return new string(pips);
    }

    /// <summary>
    /// A hint was authored for this challenge. Drives whether the row's Hint button is offered
    /// or shown as unavailable — an enabled button that reveals nothing is a false affordance.
    /// </summary>
    public bool HasHint => !string.IsNullOrWhiteSpace(Hint);

    /// <summary>
    /// True only when this challenge's GUID appears in the repo's master list. Locally authored
    /// challenges are always Custom and are badged as such, so they can never be mistaken for
    /// part of the shipped set.
    /// </summary>
    public bool IsOfficial => Source == ChallengeSource.Official;

    /// <summary>
    /// Every challenge must carry a name AND a description. Authored entries missing either are
    /// flagged in red in the dev UI rather than silently shipping a blank completion toast.
    /// </summary>
    public bool HasDetails =>
        !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Detail);

    /// <summary>
    /// Manual challenges have no detector, so nothing can complete them now that user marking
    /// is gone. The UI says so instead of pretending they are achievable.
    /// </summary>
    public bool HasDetector => Kind != ChallengeKind.Manual;
}

/// <summary>
/// The built-in challenge list, plus the merge logic that folds in user-authored challenges.
///
/// <para><b>Built-in GUIDs are frozen.</b> The literals below are permanent identity. Never
/// change one, never reuse one, and never renumber a challenge by editing its GUID — change
/// <see cref="BuiltInSortOrder"/> instead. Editing a GUID orphans every user's completion of
/// that challenge, which is precisely the data loss the GUID scheme exists to prevent.</para>
/// </summary>
public static class ChallengeCatalog
{
    // ── Frozen identity for the built-ins ────────────────────────────────────
    private const string GCombatSolo50   = "9f2a1c40-0001-4b6a-9f01-0c1a7d2b5001";
    private const string GCombatNoDamage = "9f2a1c40-0002-4b6a-9f01-0c1a7d2b5002";
    private const string GCombatMinIlvl  = "9f2a1c40-0003-4b6a-9f01-0c1a7d2b5003";
    private const string GExploreAether  = "9f2a1c40-0004-4b6a-9f01-0c1a7d2b5004";
    private const string GExploreSwim    = "9f2a1c40-0005-4b6a-9f01-0c1a7d2b5005";
    private const string GGatherBigFish  = "9f2a1c40-0006-4b6a-9f01-0c1a7d2b5006";
    private const string GGatherNoBuffs  = "9f2a1c40-0007-4b6a-9f01-0c1a7d2b5007";
    private const string GCraftHqNoMat   = "9f2a1c40-0008-4b6a-9f01-0c1a7d2b5008";
    private const string GCraftOneMacro  = "9f2a1c40-0009-4b6a-9f01-0c1a7d2b5009";
    private const string GSocialAlliance = "9f2a1c40-000a-4b6a-9f01-0c1a7d2b5010";
    private const string GSocialGate     = "9f2a1c40-000b-4b6a-9f01-0c1a7d2b5011";
    private const string GMiscGlamZone   = "9f2a1c40-000c-4b6a-9f01-0c1a7d2b5012";

    /// <summary>
    /// Old slug id → frozen GUID. Used once, by migration, so completions recorded before the
    /// GUID switch are carried across instead of lost. Never remove an entry from this map —
    /// a user may not have launched the plugin since before the change.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyIdMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["combat-solo-lv50-dungeon"]         = GCombatSolo50,
            ["combat-no-damage-trial"]           = GCombatNoDamage,
            ["combat-lowest-ilvl-ex"]            = GCombatMinIlvl,
            ["explore-all-aetherytes-lanoscea"]  = GExploreAether,
            ["explore-swim-to-sirensong"]        = GExploreSwim,
            ["gather-big-fish-il-mheg"]          = GGatherBigFish,
            ["gather-node-no-buffs"]             = GGatherNoBuffs,
            ["craft-hq-no-materia"]              = GCraftHqNoMat,
            ["craft-one-macro-run"]              = GCraftOneMacro,
            ["social-fc-alliance-raid"]          = GSocialAlliance,
            ["social-gate-gold"]                 = GSocialGate,
            ["misc-glam-one-zone"]               = GMiscGlamZone,
        };

    /// <summary>Built-in sort positions. Change these freely — they are presentation only.</summary>
    private static readonly Dictionary<string, int> BuiltInSortOrder = new(StringComparer.Ordinal)
    {
        [GCombatSolo50]   = 1,  [GCombatNoDamage] = 2,  [GCombatMinIlvl]  = 3,
        [GExploreAether]  = 4,  [GExploreSwim]    = 5,
        [GGatherBigFish]  = 6,  [GGatherNoBuffs]  = 7,
        [GCraftHqNoMat]   = 8,  [GCraftOneMacro]  = 9,
        [GSocialAlliance] = 10, [GSocialGate]     = 11,
        [GMiscGlamZone]   = 12,
    };

    /// <summary>
    /// Set once by Plugin at startup. Holds the challenges synced from the public repo, and is
    /// the authority on which GUIDs are official.
    /// </summary>
    public static OfficialCatalog? Official { get; set; }

    /// <summary>
    /// RETIRED. These twelve were placeholders with no detectors, so once manual marking was
    /// removed nothing could ever complete them. Real content now arrives by syncing the public
    /// repo (see <see cref="ChallengeSyncService"/>), so the list is deliberately empty rather
    /// than shipping challenges a player cannot finish.
    ///
    /// The GUID constants and <see cref="LegacyIdMap"/> above are kept on purpose: a user who
    /// last ran a pre-GUID build still needs their old ids translated during migration, and
    /// deleting the map would silently drop that progress.
    /// </summary>
    public static readonly IReadOnlyList<ChallengeDef> BuiltIn = new List<ChallengeDef>();

    private static readonly IReadOnlyList<ChallengeDef> RetiredBuiltIns = new List<ChallengeDef>
    {
        new(GCombatSolo50, "Combat",
            "Solo a level 50 dungeon",
            "Unsynced is fine. No Trust, no Duty Support, no party."),

        new(GCombatNoDamage, "Combat",
            "Clear a trial without taking damage",
            "Any trial. One tick of damage and the run is void."),

        new(GCombatMinIlvl, "Combat",
            "Clear an Extreme at minimum item level",
            "Sync down as far as the duty allows before entering."),

        new(GExploreAether, "Exploration",
            "Attune to every aetheryte in La Noscea",
            "Includes the aethernet shards in Limsa Lominsa."),

        new(GExploreSwim, "Exploration",
            "Reach the far edge of every open-water zone",
            "Swim, don't fly. Find where the invisible wall actually is."),

        new(GGatherBigFish, "Gathering",
            "Land a Big Fish in Il Mheg",
            "Weather and time windows apply. Bring patience."),

        new(GGatherNoBuffs, "Gathering",
            "Gather a full unspoiled node with no gathering buffs",
            "No Yield, no Gather Rate, no King's Yield. Raw perception only."),

        new(GCraftHqNoMat, "Crafting",
            "Craft an HQ level-cap item with no materia melds",
            "Base gear stats only. Food and potions allowed."),

        new(GCraftOneMacro, "Crafting",
            "Complete a full crafting rotation in a single macro",
            "No manual clicks after the first. It either works or it doesn't."),

        new(GSocialAlliance, "Social",
            "Clear an Alliance Raid with a full FC party",
            "All eight of your alliance slots filled by FC members."),

        new(GSocialGate, "Social",
            "Take first place in a Gold Saucer GATE",
            "Any GATE counts, including Any Way the Wind Blows."),

        new(GMiscGlamZone, "Miscellaneous",
            "Build a full glamour from one zone's drops",
            "Every visible slot sourced from a single zone's dungeons or FATEs."),
    };

    /// <summary>A fresh permanent identity for a newly authored challenge.</summary>
    public static string NewId() => Guid.NewGuid().ToString("D");

    /// <summary>
    /// How many challenges were withheld because they need a newer plugin, and the highest
    /// version any of them asks for. Recomputed by <see cref="Combined"/>.
    /// </summary>
    public static int     IncompatibleCount   { get; private set; }
    public static Version HighestRequired     { get; private set; } = new(0, 0, 0, 0);

    /// <summary>
    /// Can this build load the challenge? A challenge stamped with a version newer than the
    /// running plugin is withheld — it may rely on a kind or field this build cannot evaluate,
    /// and a challenge that silently never fires is worse than one that says "update first".
    /// </summary>
    public static bool IsCompatible(CustomChallenge c) =>
        RequiredVersion(c) <= PluginVersion.Current;

    /// <summary>
    /// Does this kind have a quantity worth showing as "2/4"? Only the multi-area kinds do —
    /// everything else is a single condition that is either met or not.
    /// </summary>
    public static bool HasStepProgress(ChallengeKind kind) =>
        kind is ChallengeKind.VisitAreas or ChallengeKind.VisitAreasInOrder or ChallengeKind.InArea;

    /// <summary>
    /// Does this composite mode actually have several stops to count through? A
    /// <see cref="AreaMode.Single"/> challenge is one condition set — "1/1" is noise, not progress.
    /// </summary>
    public static bool HasStepProgress(CustomChallenge c) =>
        c.Kind == ChallengeKind.InArea
            ? c.Mode != AreaMode.Single && c.StopCount > 1
            : HasStepProgress(c.Kind);

    /// <summary>
    /// The plugin version a challenge genuinely REQUIRES, derived from its content.
    ///
    /// <para><b>This must never be "the version I happened to save it on."</b> That was the
    /// original rule and it was wrong: re-saving a challenge to fix a typo stamped it with the
    /// current build and withheld it from everyone who had not updated. A cosmetic edit would
    /// silently delete a live challenge from most players' lists.</para>
    ///
    /// <para>What actually forces a requirement is a <see cref="ChallengeKind"/> an older build
    /// cannot evaluate — it would fail <c>IsWellFormed</c>, be skipped, and never fire, which is
    /// exactly the silent mis-tracking the gate exists to prevent. Added FIELDS do not: Newtonsoft
    /// ignores properties a build does not know, so an old plugin reading a challenge with a Hint
    /// simply shows no hint. Only raise a baseline for a field an old build would MISREAD rather
    /// than ignore.</para>
    ///
    /// <para>Every kind that exists today shipped together in 0.1.0 (commit 48459e7), so every
    /// current challenge is loadable by every build. An unlisted kind is by definition new and
    /// defaults to the running version — the safe direction for a value someone forgot to add.</para>
    /// </summary>
    public static Version RequiredFor(ChallengeKind kind) => kind switch
    {
        ChallengeKind.Manual
            or ChallengeKind.VisitAreas
            or ChallengeKind.VisitAreasInOrder
            or ChallengeKind.EmoteAtArea
            or ChallengeKind.MountInArea
            or ChallengeKind.GearInArea => new Version(0, 1, 0, 0),

        // The composite kind. An older build has no evaluator for it, so a challenge authored as
        // InArea must be withheld from that build rather than loaded and silently never fired.
        // This is also why legacy challenges are NOT rewritten into this kind — see the remarks on
        // ChallengeKind.InArea.
        ChallengeKind.InArea => new Version(0, 81, 33, 0),

        // The race kind. Same reasoning as InArea: an older build has no state machine for it and
        // would load a challenge it can never advance.
        ChallengeKind.RaceTimer => new Version(0, 81, 34, 0),

        _ => PluginVersion.Current,
    };

    /// <summary>
    /// What a challenge's CONTENT requires, not just its kind.
    ///
    /// <para><b>A chain needs this and the kind switch cannot see it.</b> A quest chain's own Kind
    /// is <see cref="ChallengeKind.InArea"/>, so a build that predates chains would load it
    /// happily, ignore the <c>ChainSteps</c> property it has never heard of, find the challenge's
    /// own <c>Requirements</c> list empty, fail <c>IsWellFormed</c> and skip it forever. That is
    /// precisely the silent mis-tracking the version gate exists to prevent, so the floor has to be
    /// raised by the presence of steps rather than by the kind.</para>
    ///
    /// <para>Always take the HIGHEST requirement any part of the content implies.</para>
    /// </summary>
    public static Version RequiredFor(CustomChallenge c)
    {
        var required = RequiredFor(c.Kind);

        if (c.IsChain)
        {
            var chains = new Version(0, 81, 35, 0);
            if (chains > required) required = chains;
        }

        return required;
    }

    /// <summary>Parsed requirement, or 0.0.0.0 when absent or malformed (i.e. always loadable).</summary>
    public static Version RequiredVersion(CustomChallenge c)
    {
        if (string.IsNullOrWhiteSpace(c.MinPluginVersion)) return new Version(0, 0, 0, 0);
        return Version.TryParse(c.MinPluginVersion, out var v) ? v : new Version(0, 0, 0, 0);
    }

    /// <summary>True if the string is a real GUID — used to spot pre-GUID ids during migration.</summary>
    public static bool IsGuid(string? id) => Guid.TryParse(id, out _);

    // ── Chains ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The step a chain is currently on, clamped into range, or null when it is not a chain.
    ///
    /// <para>Clamped rather than trusted: a chain edited down to fewer steps must not leave a
    /// player pointing past the end of it. Reading past the end would either throw or silently
    /// read as "finished", and neither is a completion anyone earned.</para>
    /// </summary>
    public static ChainStep? CurrentStep(CustomChallenge c)
    {
        if (!c.IsChain) return null;

        int idx = Math.Clamp(Plugin.Progress.ChainStep(c.Id), 0, c.ChainSteps.Count - 1);
        return c.ChainSteps[idx];
    }

    /// <summary>Which step number the player is on, 1-based. 0 when not a chain.</summary>
    public static int CurrentStepNumber(CustomChallenge c) =>
        c.IsChain ? Math.Clamp(Plugin.Progress.ChainStep(c.Id), 0, c.ChainSteps.Count - 1) + 1 : 0;

    /// <summary>
    /// The zone a challenge should be FILED under right now.
    ///
    /// <para>For a chain that is wherever the current step points, not where the chain was
    /// authored — a quest that walks the player from Gridania to Ul'dah belongs under Ul'dah once
    /// they are on that leg. Everything else is simply its own territory.</para>
    /// </summary>
    public static ushort EffectiveTerritory(CustomChallenge c)
    {
        var step = CurrentStep(c);
        if (step != null && step.TerritoryId != 0) return step.TerritoryId;
        return c.TerritoryId;
    }

    /// <summary>
    /// How a chain presents itself right now: the CURRENT step's wording, not the chain's.
    /// A chain's own Title/Detail are the series name and blurb; the row has to show the leg the
    /// player is actually on, or a five-step quest reads identically at every stage.
    /// </summary>
    public static (string Title, string Detail, string Hint) FaceOf(CustomChallenge c)
    {
        var step = CurrentStep(c);
        if (step == null) return (c.Title ?? string.Empty, c.Detail ?? string.Empty, c.Hint ?? string.Empty);

        // A step that leaves a field blank falls back to the chain's — an author who writes one
        // hint for the whole quest should not have it vanish on step two.
        return (
            string.IsNullOrWhiteSpace(step.Title)  ? c.Title  ?? string.Empty : step.Title,
            string.IsNullOrWhiteSpace(step.Detail) ? c.Detail ?? string.Empty : step.Detail,
            string.IsNullOrWhiteSpace(step.Hint)   ? c.Hint   ?? string.Empty : step.Hint);
    }

    /// <summary>
    /// Built-in and user-authored challenges together, in the player's chosen order. The Number
    /// stamped on each def is its 1-based position in THAT order, NOT an identity — completion is
    /// keyed by GUID alone, so re-sorting renumbers everything and costs nothing.
    /// </summary>
    public static List<ChallengeDef> Combined(Configuration cfg)
    {
        var list = new List<(int Sort, ChallengeDef Def)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int incompatible = 0;
        var highest = new Version(0, 0, 0, 0);

        bool Loadable(CustomChallenge c)
        {
            if (IsCompatible(c)) return true;
            incompatible++;
            var req = RequiredVersion(c);
            if (req > highest) highest = req;
            return false;
        }

        // Official first. These win on GUID collision: a local file must never be able to
        // shadow or redefine a challenge from the repo.
        var official = Official;
        if (official != null)
        {
            foreach (var o in official.Challenges)
            {
                if (string.IsNullOrWhiteSpace(o.Id)) continue;
                if (!Loadable(o)) continue;
                if (!seen.Add(o.Id)) continue;

                var (oTitle, oDetail, oHint) = FaceOf(o);
                list.Add((o.SortOrder, new ChallengeDef(
                    o.Id,
                    string.IsNullOrWhiteSpace(o.Category) ? "Miscellaneous" : o.Category,
                    oTitle,
                    oDetail,
                    o.Kind,
                    IsCustom: false,
                    Source:     ChallengeSource.Official,
                    Hint:       oHint,
                    Difficulty: o.Difficulty,
                    Theme:      o.Theme,
                    StepNumber: CurrentStepNumber(o),
                    StepTotal:  o.ChainSteps?.Count ?? 0,
                    ShowProgress: o.ShowProgress)));
            }
        }

        foreach (var c in cfg.CustomChallenges)
        {
            if (string.IsNullOrWhiteSpace(c.Id)) continue;
            if (!Loadable(c)) continue;
            if (!seen.Add(c.Id)) continue;   // an official challenge already owns this GUID

            // Source is decided by the master list, never by the local file. Hand-editing a
            // local challenge to carry an official GUID gets it dropped by the check above;
            // anything else is Custom and is badged as such.
            var source = official != null && official.IsOfficial(c.Id)
                ? ChallengeSource.Official
                : ChallengeSource.Custom;

            var (cTitle, cDetail, cHint) = FaceOf(c);
            list.Add((c.SortOrder, new ChallengeDef(
                c.Id,
                string.IsNullOrWhiteSpace(c.Category) ? "Miscellaneous" : c.Category,
                cTitle,
                cDetail,
                c.Kind,
                IsCustom:   source == ChallengeSource.Custom,
                Source:     source,
                Hint:       cHint,
                Difficulty: c.Difficulty,
                Theme:      c.Theme,
                StepNumber: CurrentStepNumber(c),
                StepTotal:  c.ChainSteps?.Count ?? 0,
                ShowProgress: c.ShowProgress)));
        }

        // ── Ordering ─────────────────────────────────────────────────────────
        //
        // Creation order is the tiebreaker of last resort in every mode, and it falls back to the
        // GUID so the comparison is TOTAL. That matters: List.Sort is unstable, so any comparison
        // that can return 0 for two different challenges lets them swap places between frames,
        // and the renumbering below would make the list visibly flicker.
        static int ByCreated((int Sort, ChallengeDef Def) a, (int Sort, ChallengeDef Def) b) =>
            a.Sort != b.Sort
                ? a.Sort.CompareTo(b.Sort)
                : string.CompareOrdinal(a.Def.Id, b.Def.Id);

        static int ByTitle((int Sort, ChallengeDef Def) a, (int Sort, ChallengeDef Def) b)
        {
            int t = string.Compare(a.Def.Title, b.Def.Title, StringComparison.OrdinalIgnoreCase);
            return t != 0 ? t : ByCreated(a, b);
        }

        int ByDifficulty((int Sort, ChallengeDef Def) a, (int Sort, ChallengeDef Def) b)
        {
            // Unrated sinks to the bottom instead of leading as "0 stars". A missing rating means
            // "nobody has judged this yet", which is not the same claim as "this is the easiest".
            int da = a.Def.HasDifficulty ? a.Def.Difficulty : int.MaxValue;
            int db = b.Def.HasDifficulty ? b.Def.Difficulty : int.MaxValue;
            if (da != db) return da.CompareTo(db);

            // Within one star rating, fall back to whichever of the two plain orders the player
            // last chose — so switching to Difficulty preserves the arrangement they were reading.
            return cfg.SecondarySort == ChallengeSort.Alphabetical ? ByTitle(a, b) : ByCreated(a, b);
        }

        Comparison<(int Sort, ChallengeDef Def)> order = cfg.SortMode switch
        {
            ChallengeSort.Alphabetical => ByTitle,
            ChallengeSort.Difficulty   => ByDifficulty,
            _                          => ByCreated,
        };

        list.Sort(order);

        IncompatibleCount = incompatible;
        HighestRequired   = highest;

        // Number is the position in the list the player is actually looking at — always 1..N, no
        // gaps, renumbered on every sort change. It used to be the raw SortOrder, which is sparse
        // (authoring leaves holes) and meaningless once the order is not creation order.
        var result = new List<ChallengeDef>(list.Count);
        for (int i = 0; i < list.Count; i++)
            result.Add(list[i].Def with { Number = i + 1 });

        return result;
    }

    /// <summary>The next free sort number, for a newly authored challenge.</summary>
    public static int NextSortOrder(Configuration cfg)
    {
        int max = 0;
        foreach (var s in BuiltInSortOrder.Values) if (s > max && s != int.MaxValue) max = s;
        foreach (var c in cfg.CustomChallenges)    if (c.SortOrder > max) max = c.SortOrder;
        return max + 1;
    }

    /// <summary>
    /// The definition behind a GUID — official first, then locally authored. The tracker needs
    /// this to evaluate synced challenges, not just local ones.
    /// </summary>
    public static CustomChallenge? FindCustom(Configuration cfg, string id)
    {
        var official = Official;
        if (official != null)
        {
            foreach (var o in official.Challenges)
                if (string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase)) return o;
        }

        foreach (var c in cfg.CustomChallenges)
            if (string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    /// <summary>
    /// Every definition the tracker should consider — official and local, official winning on a
    /// GUID collision. This is what <see cref="ChallengeTracker"/> iterates.
    /// </summary>
    public static List<CustomChallenge> AllTrackable(Configuration cfg)
    {
        var list = new List<CustomChallenge>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Version-gated here too: the tracker must never evaluate a challenge this build cannot
        // correctly interpret.
        var official = Official;
        if (official != null)
        {
            foreach (var o in official.Challenges)
                if (!string.IsNullOrWhiteSpace(o.Id) && IsCompatible(o) && seen.Add(o.Id)) list.Add(o);
        }

        foreach (var c in cfg.CustomChallenges)
            if (!string.IsNullOrWhiteSpace(c.Id) && IsCompatible(c) && seen.Add(c.Id)) list.Add(c);

        return list;
    }

    /// <summary>The sort number shown to the player, or 0 if the GUID is unknown.</summary>
    public static int DisplayNumber(Configuration cfg, string id)
    {
        foreach (var def in Combined(cfg))
            if (string.Equals(def.Id, id, StringComparison.OrdinalIgnoreCase)) return def.Number;
        return 0;
    }

    /// <summary>
    /// The category list, in display order: published categories first in the order the repo
    /// gives them, then locally created ones, then anything a challenge names that neither list
    /// mentions.
    ///
    /// <para>That last group is the backstop, and it is what the whole list used to be. It keeps
    /// a challenge from becoming unreachable because its category was deleted or was never
    /// declared — a challenge must always live somewhere it can be seen.</para>
    /// </summary>
    /// <param name="includeEmpty">
    /// Keep categories that currently hold no challenges. True while authoring — an empty
    /// category is the whole point of being able to create one ahead of its content. False for
    /// the player-facing list, where an empty category is a dead end with nothing behind it.
    /// </param>
    public static List<string> Categories(Configuration cfg, bool includeEmpty = false)
    {
        var seen  = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<string>();

        void Offer(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (seen.Add(name)) order.Add(name);
        }

        var official = Official;
        if (official != null)
            foreach (var name in official.Categories) Offer(name);

        if (cfg.CustomCategories != null)
            foreach (var name in cfg.CustomCategories) Offer(name);

        var populated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in Combined(cfg))
        {
            populated.Add(def.Category);
            Offer(def.Category);
        }

        if (includeEmpty) return order;

        var trimmed = new List<string>(order.Count);
        foreach (var name in order)
            if (populated.Contains(name)) trimmed.Add(name);

        return trimmed;
    }

    public static List<ChallengeDef> InCategory(Configuration cfg, string category)
    {
        var list = new List<ChallengeDef>();
        foreach (var def in Combined(cfg))
            if (string.Equals(def.Category, category, StringComparison.Ordinal)) list.Add(def);
        return list;
    }

    public static (int done, int total) OverallProgress(Configuration cfg, CompletionStore store)
    {
        int done = 0, total = 0;
        foreach (var def in Combined(cfg))
        {
            total++;
            if (store.IsComplete(def.Id)) done++;
        }
        return (done, total);
    }

    public static (int done, int total) CategoryProgress(Configuration cfg, CompletionStore store, string category)
    {
        int done = 0, total = 0;
        foreach (var def in Combined(cfg))
        {
            if (!string.Equals(def.Category, category, StringComparison.Ordinal)) continue;
            total++;
            if (store.IsComplete(def.Id)) done++;
        }
        return (done, total);
    }

    public static float Percent(int done, int total) => total > 0 ? (float)done / total : 0f;
}
