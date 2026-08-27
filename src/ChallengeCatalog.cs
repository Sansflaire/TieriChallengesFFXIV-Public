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
    bool          ShowProgress = true,
    bool          AllowMapPin = false)
{
    /// <summary>
    /// A quest chain. Title/Detail/Hint above are the CHALLENGE's own — the series name and blurb —
    /// never the current step's. Which leg the player is on travels separately, in
    /// <see cref="StepLabel"/>, so a row always says what challenge it is.
    /// </summary>
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
    /// <remarks>
    /// Assigning invalidates the built-list cache. Every OTHER input to that cache moves
    /// <see cref="Configuration.StateVersion"/>, but this one is a static with no config behind
    /// it — and it is set during startup, before the first frame, which is exactly when a stale
    /// entry would be built and then served for the rest of the session.
    /// </remarks>
    public static OfficialCatalog? Official
    {
        get => _official;
        set { _official = value; InvalidateCache(); }
    }

    private static OfficialCatalog? _official;

    // ── Built-list cache ─────────────────────────────────────────────────────
    //
    // Combined() is not cheap: two collections, a full sort, and a ChallengeDef allocated twice
    // per challenge (once built, once cloned by `with { Number }`). It is also called a great many
    // times per frame — CategoryProgress calls it once per category row, and Categories,
    // OverallProgress, InCategory, InZone, Tally and DisplayNumber each call it again. Rebuilding
    // an identical list a dozen times per frame is pure allocation churn in a plugin whose whole
    // stated design goal is to be cheap.
    //
    // The key is every input the result actually depends on. Definitions, the official catalogue,
    // and a chain's current step (which changes the face of a row) all move StateVersion; the two
    // sort settings are named explicitly rather than trusted to bump it, because they are ordinary
    // settings and nothing about changing one says "definitions changed".

    private static IReadOnlyList<ChallengeDef>? _combinedCache;
    private static int                          _combinedVersion   = int.MinValue;
    private static ChallengeSort                _combinedSort;
    private static ChallengeSort                _combinedSecondary;

    private static Dictionary<string, CustomChallenge>? _byId;
    private static int                                  _byIdVersion = int.MinValue;

    /// <summary>Drop both caches. Cheap; the next read rebuilds.</summary>
    public static void InvalidateCache()
    {
        _combinedCache   = null;
        _combinedVersion = int.MinValue;
        _byId            = null;
        _byIdVersion     = int.MinValue;
    }

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

    // ── Zone/area agreement ──────────────────────────────────────────────────

    /// <summary>
    /// Make a challenge's recorded zone — and each of its chain steps' — agree with where its areas
    /// were actually captured. Returns true if anything was rewritten.
    /// </summary>
    /// <remarks>
    /// <para><b>This only ever touches a challenge that cannot possibly work.</b> The territory says
    /// where the tracker will evaluate; the area's captured map says where the coordinates are. When
    /// they agree, nothing happens here. When they disagree the challenge is untrackable in both
    /// directions at once — never evaluated in the zone the player is standing in, and never inside
    /// the volume in the zone it is filed under — so there is no working configuration to break.</para>
    ///
    /// <para><b>How the disagreement arises.</b> The zone is captured once, when the first area is
    /// placed, and deliberately never re-read from the player afterwards, so that editing a Gridania
    /// challenge while standing in Limsa does not relocate it. That rule is right, but it protects
    /// the wrong thing for a CHAIN STEP: a new step inherits the challenge's zone, and the whole
    /// purpose of a step is to be somewhere else. Capture its position in another zone and the area
    /// records the new map while the step keeps the inherited territory.</para>
    ///
    /// <para>The map wins because it was MEASURED, standing on the spot. The territory was inherited.
    /// An area with no captured map id says nothing either way and is left alone.</para>
    /// </remarks>
    public static bool RebindZonesToAreas(CustomChallenge c)
    {
        if (c == null) return false;

        bool changed = false;

        if (c.ChainSteps != null)
            foreach (var step in c.ChainSteps)
                changed |= RebindStep(step);

        // A chain's own territory is not derived from areas at all — its STEPS carry the content,
        // and its own Areas/Requirements are either empty or (for anything authored before the
        // Creator stopped copying the shared editor buffer) a stray duplicate of one step. Deriving
        // from those would file the whole quest under whichever step was last open in the editor.
        // Where a quest STARTS is the meaningful answer, so take the first step's zone.
        if (c.IsChain)
        {
            var    steps = c.ChainSteps;
            ushort first = steps is { Count: > 0 } && steps[0] != null ? steps[0].TerritoryId : (ushort)0;
            if (first != 0 && first != c.TerritoryId)
            {
                c.TerritoryId   = first;
                c.TerritoryName = PlayerStateReader.ZoneName(first);
                changed = true;
            }

            // Drop the stray top-level volume older drafts copied out of the shared editor buffer.
            // Provably unread for a chain — the tracker dispatches IsChain ahead of Kind,
            // CompositeIsWellFormed asks about ChainSteps, and MapPinService resolves the current
            // step first — so this is a phantom that would only ever be published and puzzled over.
            if (c.Areas.Count > 0)
            {
                Plugin.Log.Information(
                    $"[Zones] \"{c.Title}\": dropped {c.Areas.Count} stray top-level area(s); "
                  + "a chain's content lives on its steps.");
                c.Areas.Clear();
                changed = true;
            }

            return changed;
        }

        // The challenge's own areas, for the non-chain kinds. Same reasoning: a challenge whose
        // territory disagrees with its captured position is one that can never fire.
        ushort own = TerritoryFromAreas(c.Requirements, c.Areas);
        if (own != 0 && own != c.TerritoryId)
        {
            Plugin.Log.Information(
                $"[Zones] \"{c.Title}\": zone {c.TerritoryId} disagreed with its captured position "
              + $"(really territory {own}); rebound.");

            c.TerritoryId   = own;
            c.TerritoryName = PlayerStateReader.ZoneName(own);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// <see cref="RebindZonesToAreas"/> for a single step, for the Creator to call while authoring.
    /// </summary>
    public static bool RebindStepZone(ChainStep? step) => RebindStep(step);

    private static bool RebindStep(ChainStep? step)
    {
        if (step == null) return false;

        ushort fromMap = TerritoryFromAreas(step.Requirements, null);
        if (fromMap == 0 || fromMap == step.TerritoryId) return false;

        Plugin.Log.Information(
            $"[Zones] step \"{step.Title}\": zone {step.TerritoryId} disagreed with its captured "
          + $"position (really territory {fromMap}); rebound.");

        step.TerritoryId   = fromMap;
        step.TerritoryName = PlayerStateReader.ZoneName(fromMap);
        return true;
    }

    /// <summary>The territory implied by the first area carrying a captured map id, or 0.</summary>
    private static ushort TerritoryFromAreas(
        List<AreaRequirement>? reqs, List<ChallengeArea>? areas)
    {
        if (reqs != null)
            foreach (var r in reqs)
                if (r?.Area is { MapId: not 0 })
                    return PlayerStateReader.TerritoryOfMap(r.Area.MapId);

        if (areas != null)
            foreach (var a in areas)
                if (a is { MapId: not 0 })
                    return PlayerStateReader.TerritoryOfMap(a.MapId);

        return 0;
    }

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

    // There was a FaceOf() here that made a chain present as its CURRENT STEP — the row's title was
    // the step's title, so a quest read "Step 2" in the list with the challenge's real name nowhere
    // on screen. Trist's call, 2026-08-26: a row identifies the CHALLENGE, and the step is context
    // the row carries alongside it, never a replacement for its name. Combined now stamps the
    // challenge's own Title/Detail/Hint, and how far through it is travels separately as StepLabel.

    /// <summary>
    /// Built-in and user-authored challenges together, in the player's chosen order. The Number
    /// stamped on each def is its 1-based position in THAT order, NOT an identity — completion is
    /// keyed by GUID alone, so re-sorting renumbers everything and costs nothing.
    /// </summary>
    public static IReadOnlyList<ChallengeDef> Combined(Configuration cfg)
    {
        if (_combinedCache != null
            && _combinedVersion   == cfg.StateVersion
            && _combinedSort      == cfg.SortMode
            && _combinedSecondary == cfg.SecondarySort)
            return _combinedCache;

        var built = BuildCombined(cfg);

        _combinedCache     = built;
        _combinedVersion   = cfg.StateVersion;
        _combinedSort      = cfg.SortMode;
        _combinedSecondary = cfg.SecondarySort;

        return built;
    }

    private static List<ChallengeDef> BuildCombined(Configuration cfg)
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

                list.Add((o.SortOrder, new ChallengeDef(
                    o.Id,
                    string.IsNullOrWhiteSpace(o.Category) ? "Miscellaneous" : o.Category,
                    o.Title  ?? string.Empty,
                    o.Detail ?? string.Empty,
                    o.Kind,
                    IsCustom: false,
                    Source:     ChallengeSource.Official,
                    Hint:       o.Hint ?? string.Empty,
                    Difficulty: o.Difficulty,
                    Theme:      o.Theme,
                    StepNumber: CurrentStepNumber(o),
                    StepTotal:  o.ChainSteps?.Count ?? 0,
                    ShowProgress: o.ShowProgress,
                    AllowMapPin:  o.AllowMapPin)));
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

            list.Add((c.SortOrder, new ChallengeDef(
                c.Id,
                string.IsNullOrWhiteSpace(c.Category) ? "Miscellaneous" : c.Category,
                c.Title  ?? string.Empty,
                c.Detail ?? string.Empty,
                c.Kind,
                IsCustom:   source == ChallengeSource.Custom,
                Source:     source,
                Hint:       c.Hint ?? string.Empty,
                Difficulty: c.Difficulty,
                Theme:      c.Theme,
                StepNumber: CurrentStepNumber(c),
                StepTotal:  c.ChainSteps?.Count ?? 0,
                ShowProgress: c.ShowProgress,
                AllowMapPin:  c.AllowMapPin)));
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
    /// <remarks>
    /// Indexed rather than scanned. A challenge row asks this three separate times per frame — for
    /// the spoiler mask, for the same-zone pin button, and for the chain-progress line — and the
    /// zone view asks it once per definition inside a loop over every definition, which is a linear
    /// search nested in a linear search. Official still wins a GUID collision: it is inserted first
    /// and local entries do not overwrite.
    /// </remarks>
    public static CustomChallenge? FindCustom(Configuration cfg, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;

        if (_byId == null || _byIdVersion != cfg.StateVersion)
        {
            var map = new Dictionary<string, CustomChallenge>(StringComparer.OrdinalIgnoreCase);

            var official = Official;
            if (official != null)
                foreach (var o in official.Challenges)
                    if (!string.IsNullOrWhiteSpace(o.Id)) map.TryAdd(o.Id, o);

            foreach (var c in cfg.CustomChallenges)
                if (!string.IsNullOrWhiteSpace(c.Id)) map.TryAdd(c.Id, c);

            _byId        = map;
            _byIdVersion = cfg.StateVersion;
        }

        return _byId.TryGetValue(id, out var found) ? found : null;
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
