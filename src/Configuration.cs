using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace TieriChallengesFFXIV;

/// <summary>
/// How the master pane groups the catalogue. Persisted as an int — append new modes, never
/// renumber an existing one.
/// </summary>
public enum GroupMode
{
    /// <summary>By the author's category ("Exploration", "Miscellaneous"). The original view.</summary>
    Categories = 0,

    /// <summary>By expansion, then by zone within it — the shape of the in-game Teleport menu.</summary>
    Zones = 1,
}

/// <summary>
/// A user-authored challenge. Plain mutable class with a parameterless ctor — Dalamud
/// serializes plugin configs with Newtonsoft, and positional records round-trip badly.
///
/// Kind-specific fields live flat on this class rather than in a polymorphic hierarchy: a
/// subclass-per-kind would need Newtonsoft <c>$type</c> discriminators, and those are exactly
/// what broke TieriCharacterSelect's config on an assembly rename. Unused fields for a given
/// Kind simply sit at their defaults.
/// </summary>
[Serializable]
public sealed class CustomChallenge
{
    /// <summary>
    /// Permanent GUID. This is the only key completion is ever recorded against. Never
    /// regenerate it for an existing challenge — that orphans the user's progress.
    /// </summary>
    public string Id       { get; set; } = string.Empty;

    /// <summary>
    /// Display/sort position. Purely presentational — change it freely, it cannot affect
    /// tracking, which is exactly why identity was moved to a GUID.
    /// </summary>
    public int SortOrder   { get; set; }

    /// <summary>
    /// Minimum plugin version required to load this challenge, as <c>A.B.C.D</c>.
    ///
    /// Stamped with the authoring plugin's version at creation. A challenge authored on a newer
    /// build may use a challenge kind or field an older build cannot evaluate, so an older
    /// plugin refuses to load it rather than silently mis-tracking it or never firing it.
    /// Challenges predating this field default to <c>0.0.0.0</c> and always load.
    /// </summary>
    public string MinPluginVersion { get; set; } = "0.0.0.0";

    public string Category { get; set; } = string.Empty;
    public string Title    { get; set; } = string.Empty;
    public string Detail   { get; set; } = string.Empty;

    /// <summary>
    /// Optional nudge, shown only when the player asks for it with the row's Hint button — it
    /// replaces the description line rather than sitting next to it, so nothing is spoiled by
    /// simply scrolling the list.
    ///
    /// <para>Empty means no hint was written, and the UI says exactly that instead of offering a
    /// button that does nothing. Deliberately NOT required at authoring time: a description is
    /// mandatory, a hint is a courtesy.</para>
    /// </summary>
    public string Hint { get; set; } = string.Empty;

    /// <summary>
    /// Authored difficulty, 1–5 stars. 0 means unrated, which is the value every challenge
    /// written before this field existed deserialises to — and unrated renders no stars rather
    /// than five empty ones, so old challenges look deliberate instead of broken.
    /// </summary>
    public int Difficulty { get; set; }

    /// <summary>
    /// The player may place a map flag on this challenge's location, from the "you are in this
    /// zone" marker on its row.
    ///
    /// <para><b>Off by default, and that default is the design.</b> The catalogue's discoverability
    /// rule is zone name plus written hint — finding the exact spot is part of the challenge (see
    /// <c>docs/Road to 1.0.md</c>). This is a deliberate per-challenge opt-out of that rule, for
    /// challenges where the hunt is not the point and the doing is.</para>
    ///
    /// <para>Unrelated to the SPOILER mask, which is about story progression rather than
    /// discoverability. The two cannot collide in practice: the marker only appears while the
    /// player is standing in the zone, and being in a zone is what clears its mask.</para>
    ///
    /// <para>Deliberately does NOT raise the version floor. A build that predates this field
    /// ignores it and simply offers no pin — a missing convenience, not the silent mis-tracking
    /// <c>ChallengeCatalog.RequiredFor</c> exists to prevent.</para>
    /// </summary>
    public bool AllowMapPin { get; set; }

    /// <summary>Stored as an int; append new kinds, never renumber.</summary>
    public ChallengeKind Kind { get; set; } = ChallengeKind.Manual;

    /// <summary>
    /// Territory this challenge is evaluated in. 0 = any zone. Captured at authoring time.
    /// This is the field the tracker gates on — challenges for other zones are never even
    /// looked at, which is the single biggest saving in the whole evaluation loop.
    /// </summary>
    public ushort TerritoryId { get; set; }

    /// <summary>Human-readable zone name, captured at authoring time so the UI needn't re-resolve it.</summary>
    public string TerritoryName { get; set; } = string.Empty;

    /// <summary>Trigger volumes. VisitAreas* uses all of them; EmoteAtArea / MountInArea use the first.</summary>
    public List<ChallengeArea> Areas { get; set; } = new();

    /// <summary>
    /// Show live step progress ("2/4") for multi-step challenges. Only meaningful for kinds that
    /// have a quantity — visiting several areas. Defaults to on, and on for challenges authored
    /// before the field existed, because seeing progress is the useful default; turn it off for a
    /// challenge where revealing how many steps remain would spoil it.
    /// </summary>
    public bool ShowProgress { get; set; } = true;

    // ── EmoteAtArea ──────────────────────────────────────────────────────────
    public uint   EmoteId   { get; set; }
    public string EmoteName { get; set; } = string.Empty;

    /// <summary>When true, the emote only counts if the player is facing <see cref="FacingRadians"/>.</summary>
    public bool  RequireFacing { get; set; }

    /// <summary>
    /// Captured directly from the player's Rotation at authoring time. Deliberately stored as
    /// the game's raw value rather than a compass bearing — "stand how you want, press capture"
    /// needs no assumption about which way 0 points.
    /// </summary>
    public float FacingRadians { get; set; }

    /// <summary>Half-width of the accepted facing arc, in degrees.</summary>
    public float FacingToleranceDeg { get; set; } = 30f;

    // ── MountInArea ──────────────────────────────────────────────────────────
    public uint   MountId   { get; set; }
    public string MountName { get; set; } = string.Empty;

    // ── GearInArea ───────────────────────────────────────────────────────────
    public GearRequirement GearMode { get; set; } = GearRequirement.FullOutfit;

    /// <summary>MirageStoreSetItem container row id when <see cref="GearMode"/> is FullOutfit.</summary>
    public uint   OutfitSetId { get; set; }
    public string OutfitName  { get; set; } = string.Empty;

    /// <summary>Item row id when <see cref="GearMode"/> is SingleItem.</summary>
    public uint   GearItemId   { get; set; }
    public string GearItemName { get; set; } = string.Empty;

    /// <summary>
    /// GearInArea only: when true the whole zone counts and <see cref="Areas"/> is ignored.
    /// Lets "wear this outfit anywhere in Gridania" be authored without placing a volume.
    /// </summary>
    public bool WholeZone { get; set; }

    // ── InArea (the composite kind) ──────────────────────────────────────────

    /// <summary>
    /// <see cref="ChallengeKind.InArea"/> only: how many stops there are and whether their order
    /// matters. Ignored by every legacy kind.
    /// </summary>
    public AreaMode Mode { get; set; } = AreaMode.Single;

    /// <summary>
    /// <see cref="ChallengeKind.InArea"/> only: the stops, each an area plus the conditions that
    /// must hold inside it.
    ///
    /// <para>Deliberately a SEPARATE list from <see cref="Areas"/> rather than a richer element
    /// type on it. <see cref="Areas"/> is loaded with meaning for five shipped kinds and is written
    /// into every published challenge file; changing its element type would rewrite the on-disk
    /// shape of challenges that older builds still need to read. A new list is invisible to them —
    /// Newtonsoft drops properties it does not know — so both shapes coexist with no migration.</para>
    /// </summary>
    public List<AreaRequirement> Requirements { get; set; } = new();

    // ── Quest chains (Blue) ──────────────────────────────────────────────────

    /// <summary>
    /// Ordered steps. Non-empty makes this a <see cref="ChallengeTheme.Quest"/>: the row shows only
    /// the CURRENT step, and only finishing the last one completes the challenge.
    ///
    /// <para>Independent of <see cref="Kind"/> — a chain's steps each carry their own
    /// <c>Requirements</c>, so the chain itself does not need a kind beyond
    /// <see cref="ChallengeKind.InArea"/> to hang them on.</para>
    /// </summary>
    public List<ChainStep> ChainSteps { get; set; } = new();

    public bool IsChain => ChainSteps is { Count: > 0 };

    /// <summary>
    /// Progress resets on logout instead of persisting. Off by default, which is the change
    /// multi-objective challenges needed: an adventure the player is told to take their time over
    /// cannot lose its progress every time they log out.
    ///
    /// <para>Turn it ON to author the old "within one login session" constraint deliberately —
    /// a route that is only interesting done in one sitting.</para>
    /// </summary>
    public bool SessionOnly { get; set; }

    /// <summary>
    /// What this challenge IS, derived from its structure. See <see cref="ChallengeTheme"/> for
    /// why there is no field backing this.
    /// </summary>
    public ChallengeTheme Theme =>
        IsChain                                              ? ChallengeTheme.Quest
        : Kind == ChallengeKind.InArea && StopCount > 1       ? ChallengeTheme.Adventure
        : ChallengeTheme.Normal;

    /// <summary>
    /// The step the player is on, or null when this is not a chain or the chain is finished.
    /// Index is clamped rather than trusted — a chain edited down to fewer steps must not leave a
    /// player pointing past the end of it.
    /// </summary>
    public ChainStep? StepAt(int index)
    {
        if (!IsChain) return null;
        if (index < 0 || index >= ChainSteps.Count) return null;
        return ChainSteps[index];
    }

    // ── RaceTimer ────────────────────────────────────────────────────────────

    /// <summary>Volume that arms the race, and re-entering which restarts the clock.</summary>
    public ChallengeArea? RaceStart { get; set; }

    /// <summary>Volume that completes the race.</summary>
    public ChallengeArea? RaceFinish { get; set; }

    /// <summary>
    /// Optional bounding volume the runner must stay INSIDE. Note the inverted sense: every other
    /// area in the plugin completes something by being entered, this one ends the run by being
    /// left. Only consulted when <see cref="RaceUseQuitArea"/> is set.
    /// </summary>
    public ChallengeArea? RaceQuit { get; set; }

    /// <summary>
    /// Whether leaving <see cref="RaceQuit"/> ends the run. Off by default — Trist's call: a race
    /// with no bounding area is a perfectly good race, and a quit volume the author forgot to size
    /// properly would end runs for reasons the player cannot see.
    /// </summary>
    public bool RaceUseQuitArea { get; set; }

    /// <summary>Seconds allowed before the run fails. 0 = untimed (finish whenever).</summary>
    public int RaceFailSeconds { get; set; }

    /// <summary>Every race volume that exists, for the overlay and for validation.</summary>
    public IEnumerable<ChallengeArea> RaceAreas()
    {
        if (RaceStart  != null) yield return RaceStart;
        if (RaceFinish != null) yield return RaceFinish;
        if (RaceUseQuitArea && RaceQuit != null) yield return RaceQuit;
    }

    public bool IsAreaKind =>
        Kind is ChallengeKind.VisitAreas
             or ChallengeKind.VisitAreasInOrder
             or ChallengeKind.EmoteAtArea
             or ChallengeKind.MountInArea
             or ChallengeKind.GearInArea
             or ChallengeKind.InArea;

    /// <summary>
    /// How many stops this challenge actually has, whichever kind it is. Used by the progress
    /// readout so a composite challenge reports "2/4" the same way VisitAreas always has.
    /// </summary>
    public int StopCount => Kind == ChallengeKind.InArea
        ? (Requirements?.Count ?? 0)
        : (Areas?.Count ?? 0);

    /// <summary>
    /// Everything the tracker needs is present. Half-authored entries are skipped rather than
    /// evaluated — a challenge with no areas would otherwise silently never fire, or worse,
    /// fire immediately.
    /// </summary>
    public bool IsWellFormed() => Kind switch
    {
        ChallengeKind.Manual            => true,
        ChallengeKind.VisitAreas        => Areas.Count > 0,
        ChallengeKind.VisitAreasInOrder => Areas.Count > 0,
        ChallengeKind.EmoteAtArea       => Areas.Count > 0 && EmoteId != 0,
        ChallengeKind.MountInArea       => Areas.Count > 0 && MountId != 0,
        ChallengeKind.GearInArea        => TerritoryId != 0
                                        && (WholeZone || Areas.Count > 0)
                                        && (GearMode == GearRequirement.FullOutfit
                                                ? OutfitSetId != 0
                                                : GearItemId != 0),
        ChallengeKind.InArea            => CompositeIsWellFormed(),
        ChallengeKind.RaceTimer         => TerritoryId != 0
                                        && RaceStart  != null
                                        && RaceFinish != null
                                        && (!RaceUseQuitArea || RaceQuit != null),
        _                               => false,
    };

    /// <summary>
    /// A composite challenge needs a zone, at least one stop, and every stop well-formed.
    /// <see cref="AreaMode.Single"/> additionally means exactly one — an author who adds a second
    /// stop without switching mode would otherwise silently ship a challenge where stops 2..N are
    /// never evaluated.
    /// </summary>
    private bool CompositeIsWellFormed()
    {
        if (TerritoryId == 0) return false;

        // A chain hangs its content on its STEPS, not on its own Requirements — so the two are
        // validated separately and a chain never needs a Requirements list of its own.
        if (IsChain)
        {
            foreach (var s in ChainSteps)
                if (s == null || !s.IsWellFormed()) return false;
            return true;
        }

        if (Requirements == null || Requirements.Count == 0) return false;
        if (Mode == AreaMode.Single && Requirements.Count != 1) return false;

        foreach (var r in Requirements)
            if (r == null || !r.IsWellFormed()) return false;

        return true;
    }
}

/// <summary>
/// Persisted plugin state. Lives at
/// <c>%APPDATA%\XIVLauncher\pluginConfigs\TieriChallengesFFXIV.json</c>.
///
/// Completion is keyed by <see cref="ChallengeDef.Id"/> — a stable string — so reordering or
/// inserting challenges in <see cref="ChallengeCatalog"/> never shifts anyone's progress.
/// Never renumber an existing Id; retire it instead.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>1 = pre-GUID (completion stored in this file). 2 = GUID + CompletionStore files.</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// LEGACY, v1 only. Completion used to live here keyed by slug ids. It now lives in
    /// <see cref="CompletionStore"/> keyed by GUID. This property is retained ONLY so an old
    /// config still deserialises and its contents can be migrated across — deleting it would
    /// silently destroy the progress of anyone who had not launched since the change.
    /// Emptied once migration has run; do not read it anywhere else.
    /// </summary>
    public Dictionary<string, bool> Completed { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Challenges authored through the dev-only Challenge Creator. These merge into the
    /// category list alongside <see cref="ChallengeCatalog.BuiltIn"/>.
    /// </summary>
    public List<CustomChallenge> CustomChallenges { get; set; } = new();

    /// <summary>
    /// Categories that exist in their own right, in display order.
    ///
    /// <para>Before this existed a category was purely a side effect of a challenge naming it, so
    /// "New category" created nothing — the name lived only on the next challenge added, and
    /// vanished the moment that challenge was deleted or recategorised. A category is now a thing
    /// you can create, keep empty, and order.</para>
    ///
    /// <para>This is the LOCAL list. Published categories arrive with the synced catalogue and
    /// are held by <see cref="OfficialCatalog"/>; the two are merged for display, official first,
    /// exactly like challenges.</para>
    /// </summary>
    public List<string> CustomCategories { get; set; } = new();

    /// <summary>
    /// Selected master-pane category, by name — never by list index, per DESIGN_SYSTEM §6.1.
    /// Empty or unknown resolves to the first available category at draw time.
    /// </summary>
    public string SelectedCategory { get; set; } = string.Empty;

    /// <summary>How the master pane groups the catalogue. Persisted as an int; append, never renumber.</summary>
    public GroupMode Grouping { get; set; } = GroupMode.Categories;

    /// <summary>
    /// Selected zone in <see cref="GroupMode.Zones"/>, as a territory id — a stable game identifier,
    /// not a list position, which is the same rule the category selection follows. -1 means nothing
    /// has been picked yet; 0 is a real selection meaning the "not tied to a zone" bucket.
    /// </summary>
    public int SelectedTerritory { get; set; } = -1;

    /// <summary>
    /// Expansions the user has collapsed, by ExVersion row id. Persisted because collapsing a
    /// 150-row list is a navigation preference — losing it on every relaunch would make the mode
    /// annoying enough to abandon.
    /// </summary>
    public List<uint> CollapsedExpansions { get; set; } = new();

    /// <summary>
    /// Hide zones with no challenges in them. Off by default: the request was for the full zone
    /// list, and a player browsing for something to do is entitled to see the empty ones. On, it
    /// turns the same list into a short index of where content actually is.
    /// </summary>
    public bool ZonesWithChallengesOnly { get; set; }

    /// <summary>
    /// DEV BUILDS ONLY. Widens the Zone tab from "reachable + authored" (~75 entries) to every
    /// zone and duty in the game (~350+), via <c>ZoneIndex.AllGameContent</c> — Trist's own census
    /// of where a challenge has NOT been written yet. Field exists in every build (harmless,
    /// mirrors <see cref="PublicPreview"/>'s pattern) but the toggle to set it only renders behind
    /// <c>#if DEV_BUILD</c>, so it can never be reached from a Release build.
    /// </summary>
    public bool DevShowAllContent { get; set; }

    /// <summary>
    /// Render the window with PanacheUI (true) or the plain-ImGui fallback (false). Only
    /// consulted when PanacheUI actually loaded — see <c>PanacheAvailability</c>. When the
    /// library is missing this is ignored and the fallback is used regardless.
    /// </summary>
    public bool UsePanacheUI { get; set; } = true;

    /// <summary>
    /// DEV BUILDS ONLY. Renders the dev plugin exactly as the public build looks — no DEV badge,
    /// no Creator button, no developer diagnostics, no "missing details" flags. It is still the
    /// dev plugin; this only hides developer affordances so the public experience can be checked
    /// without producing and installing a Release build.
    /// </summary>
    public bool PublicPreview { get; set; }

    /// <summary>When the official catalogue was last synced. Default = never.</summary>
    public DateTime LastSyncUtc { get; set; } = DateTime.MinValue;

    /// <summary>Sync the official catalogue automatically on login.</summary>
    public bool AutoSync { get; set; } = true;

    // ── Appearance ───────────────────────────────────────────────────────────

    /// <summary>
    /// Full path to an image painted behind the whole window. Empty = the plain theme background.
    ///
    /// <para>Stored as a path rather than copied into the config directory: it is the user's own
    /// file, and silently duplicating it would leave a stale copy behind when they change it.
    /// A path that no longer resolves simply falls back to the plain background.</para>
    /// </summary>
    public string BackgroundImagePath { get; set; } = string.Empty;

    /// <summary>How strongly the background image shows, 0..1. Below 1 it fades toward the theme colour.</summary>
    public float BackgroundImageOpacity { get; set; } = 1f;

    /// <summary>
    /// Opacity of the panels layered over the background image, 0..1. This is the control that
    /// makes a background image usable: at 1 the panels hide it, and at ~0.3 it reads through them.
    ///
    /// <para>Ignored entirely when no image is set — panels over a flat colour gain nothing from
    /// being translucent, and a half-transparent panel over the base colour just looks washed out.</para>
    /// </summary>
    public float PanelOpacity { get; set; } = 0.72f;

    /// <summary>
    /// When true, the window cannot be dragged by left-click. Toggled by the lock pill next to
    /// the close button. Persisted so a window positioned just right stays put across relaunches.
    /// </summary>
    public bool WindowLocked { get; set; }

    /// <summary>
    /// UI scale step for the Panache main window — 1, 2 or 3. Set from Settings → UI Scale.
    ///
    /// <para>A step rather than a free-form multiplier on purpose. PanacheUI exposes no text
    /// measurement API, so nothing in the plugin can detect that a chosen value has started
    /// clipping rows or overflowing pills; three sizes that were actually looked at beat a slider
    /// that can be dragged somewhere broken. Step 1 maps to exactly 1.0, so the default is
    /// unchanged from before this setting existed.</para>
    ///
    /// <para>Out-of-range values are clamped by <see cref="MigrateIfNeeded"/> rather than trusted:
    /// this is a hand-editable JSON file, and a 0 here would collapse the entire window to nothing.</para>
    /// </summary>
    public int UiScale { get; set; } = 1;

    /// <summary>
    /// Highest difficulty shown in the challenge list, 1–5. 5 (the default) shows everything.
    /// </summary>
    /// <remarks>
    /// <para>A ceiling, not a selection: 3 shows difficulty 1, 2 and 3 and hides 4 and 5. The
    /// control that sets it is a five-star row, so "four stars lit" reads directly as "nothing
    /// harder than four".</para>
    ///
    /// <para><b>Unrated challenges are never filtered out.</b> Difficulty 0 means "no rating was
    /// authored", not "trivial" — hiding those behind a difficulty ceiling would make a
    /// half-rated catalogue look broken, and there is no value of this setting that would bring
    /// them back except the one that disables filtering entirely.</para>
    ///
    /// <para>Clamped by <see cref="MigrateIfNeeded"/>: a hand-edited 0 here would empty the list
    /// with no visible cause.</para>
    /// </remarks>
    public int MaxDifficulty { get; set; } = 5;

    /// <summary>
    /// Local checkout of the public sync repo, used by the dev-only ban publisher. Dev machines
    /// only — a public build never reads it.
    /// </summary>
    public string SyncRepoPath { get; set; } = string.Empty;

    /// <summary>
    /// Local checkout of the PRIVATE plugin repo. The ban ledger is mirrored into its
    /// <c>backup/</c> folder on every save, so the one irreplaceable file in the ban system is
    /// version-controlled rather than living only in a config directory. Dev machines only.
    /// </summary>
    public string DevRepoPath { get; set; } = string.Empty;

    /// <summary>
    /// Suppress the bottom-right "start this race?" prompt that appears while standing in a race's
    /// start area. Set by the prompt's own "Don't show these" button; cleared from Settings.
    ///
    /// <para>Global rather than per-challenge, deliberately. The player dismissing it is saying
    /// "stop popping things up at me", not "stop popping THIS up at me" — and a per-challenge
    /// suppression list would need its own management UI to ever be undone, which is a lot of
    /// surface for a preference with one obvious meaning. Races stay startable from the challenge
    /// row either way, which is what makes suppressing it safe.</para>
    /// </summary>
    public bool RacePromptSuppressed { get; set; }

    // ── Sound ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Master cue volume, 0..1.
    ///
    /// <para><b>Applied by rescaling the audio, not by the player.</b> Three of the four cues ship
    /// as .wav files played through winmm's <c>PlaySound</c>, which takes no volume argument, and
    /// they cannot go back through the game mixer — that was proven silent over ~15 builds
    /// (BROKEN.md 003). <see cref="SoundVolume"/> is therefore honoured by writing a scaled copy of
    /// the wave and playing that. The one game-bank cue passes it straight to the engine.</para>
    /// </summary>
    public float SoundVolume { get; set; } = 1f;

    /// <summary>Silence every cue. Independent of <see cref="SoundVolume"/> so muting does not lose the level.</summary>
    public bool SoundMuted { get; set; }

    /// <summary>
    /// Cues the player has switched off individually, by <c>SoundService.Cue</c> name. Stored by
    /// name rather than ordinal so adding or reordering cues cannot silence the wrong one.
    /// </summary>
    public List<string> DisabledCues { get; set; } = new();

    // ── Notifications ────────────────────────────────────────────────────────

    /// <summary>Show the bottom-right progress notification when part of an objective lands.</summary>
    public bool ShowProgressPopups { get; set; } = true;

    /// <summary>Show the big completion banner when a challenge finishes.</summary>
    public bool ShowCompletionBanner { get; set; } = true;

    /// <summary>Show floating text over the character.</summary>
    public bool ShowFlyText { get; set; } = true;

    /// <summary>How long a notification stays on screen, in seconds. Clamped 2–15 on load.</summary>
    public float PopupSeconds { get; set; } = 5f;

    /// <summary>
    /// Hold notifications while in combat or inside a duty.
    ///
    /// <para>Off by default: a completion the player earned mid-fight should be celebrated when it
    /// happens. On, for anyone who would rather nothing appeared over a boss.</para>
    /// </summary>
    public bool SuppressInCombat { get; set; }

    // ── Colours ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Recoloured palette slots, keyed by <see cref="PaletteSlot"/> NAME. Absent = shipped default,
    /// which is what makes "Reset" a delete rather than a second copy of the defaults to keep in
    /// step with the first.
    /// </summary>
    public Dictionary<string, string> PaletteOverrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Challenge categories the filter menu is hiding, by <see cref="ChallengeFilterFlag"/> name.
    ///
    /// <para>Stored as what to HIDE rather than what to show, so a filter set before a new
    /// challenge shape existed cannot silently hide it. An empty list means "show everything",
    /// which is also what a config written before this field existed deserialises to.</para>
    /// </summary>
    public List<string> HiddenFilters { get; set; } = new();

    /// <summary>How the challenge list is ordered. Set from Settings → Sort.</summary>
    public ChallengeSort SortMode { get; set; } = ChallengeSort.Created;

    /// <summary>
    /// The last plain order the player chose — Created or Alphabetical, never Difficulty. Used as
    /// the tiebreaker between challenges of equal difficulty, so switching to Difficulty rearranges
    /// the list as little as possible: within each star band it still reads the way it just did.
    /// </summary>
    public ChallengeSort SecondarySort { get; set; } = ChallengeSort.Created;


    /// <summary>
    /// Every territory the player has ever physically been in, recorded by
    /// <see cref="AttunementService.RecordVisit"/> the moment <see cref="ChallengeTracker"/>
    /// notices the current zone changed. This is what makes a spoiler mask lift for a housing
    /// ward the player has walked through but does not own property in — FFXIV has no attunement
    /// crystal for a residential zone, so attunement alone (<c>Telepo.TeleportList</c>) can never
    /// clear one. Append-only; nothing in this plugin ever removes an entry.
    /// </summary>
    public List<uint> VisitedTerritories { get; set; } = new();

    /// <summary>Last window width/height, so the surface reopens at the size it was closed at.</summary>
    public int WindowWidth  { get; set; } = 720;
    public int WindowHeight { get; set; } = 660;

    /// <summary>
    /// Entry in <c>sound/system/SE_UI.scd</c> played when part of an objective lands.
    ///
    /// <para>Persisted rather than compiled in because not every index in the bank holds audible
    /// audio, so picking one is a matter of listening. <c>/tchallenges sfx progress &lt;n&gt;</c>
    /// auditions and sets it live.</para>
    /// </summary>
    public uint ProgressSoundEntry { get; set; } = GameSound.DefaultProgressEntry;

    /// <summary>Entry played when a challenge completes.</summary>
    public uint CompleteSoundEntry { get; set; } = GameSound.DefaultCompleteEntry;

    /// <summary>Entry played when a progress wipe is confirmed.</summary>
    public uint ResetSoundEntry { get; set; } = GameSound.DefaultResetEntry;

    // A cue is a (bank, entry) pair. The completion fanfare lives in its own zingle file rather
    // than at some index of the shared UI bank, so the path has to travel with the number —
    // an entry alone is meaningless without knowing which .scd it indexes.
    public string ProgressSoundPath { get; set; } = GameSound.DefaultProgressBank;
    public string CompleteSoundPath { get; set; } = GameSound.DefaultCompleteBank;
    public string ResetSoundPath    { get; set; } = GameSound.DefaultResetBank;

    /// <summary>Cue for arriving in a zone that still has open challenges.</summary>
    public string ZoneSoundPath  { get; set; } = GameSound.DefaultZoneBank;
    public uint   ZoneSoundEntry { get; set; } = GameSound.DefaultZoneEntry;

    /// <summary>
    /// Bumped when the shipped cue defaults change in a way that must overwrite what is on disk.
    ///
    /// <para>Needed because entries and banks are saved independently: an install carrying the
    /// old <c>CompleteSoundEntry = 55</c> would otherwise pair it with the new zingle bank and
    /// stay silent, since 55 indexes nothing there either. Version 1 resets all three cues.</para>
    /// </summary>
    public int SoundConfigVersion { get; set; }

    /// <summary>
    /// Bumped by every change that affects what the tracker should be evaluating. Deliberately
    /// `internal` so Newtonsoft leaves it out of the saved JSON — it is a runtime cache key,
    /// not persisted state.
    /// </summary>
    internal int StateVersion;

    /// <summary>Call after adding/editing/removing a challenge definition.</summary>
    public void DefinitionsChanged() => StateVersion++;

    /// <summary>
    /// One-time upgrade path. Runs on every load; does real work only once.
    ///
    /// <para><b>Contract: a plugin update must never cost a user their progress.</b> That means
    /// (a) legacy slug-keyed completions are translated to GUIDs rather than dropped,
    /// (b) authored challenges that predate GUIDs are given one and their completion follows
    /// them, and (c) everything recovered is written to the permanent ledger as well, so even a
    /// later Reset cannot lose it.</para>
    /// </summary>
    public void MigrateIfNeeded(CompletionStore store)
    {
        // A value type cannot be null, so an older config deserialises UiScale as 0 rather than
        // leaving it at the property initialiser. Clamp instead of trusting: 0 would multiply
        // every size in the window by zero.
        if (UiScale is < 1 or > 3) UiScale = 1;

        // Difficulty sorts by a field it does not itself provide a tiebreaker for, so the
        // secondary must never be Difficulty as well — that would recurse conceptually and,
        // in a hand-edited config, produce a meaningless order.
        if (SecondarySort == ChallengeSort.Difficulty) SecondarySort = ChallengeSort.Created;


        // Newtonsoft leaves absent properties null on configs written before they existed.
        Completed           ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        CustomChallenges    ??= new List<CustomChallenge>();
        SelectedCategory    ??= string.Empty;
        CollapsedExpansions ??= new List<uint>();
        CustomCategories    ??= new List<string>();
        VisitedTerritories  ??= new List<uint>();
        DisabledCues        ??= new List<string>();
        HiddenFilters       ??= new List<string>();
        PaletteOverrides    ??= new Dictionary<string, string>(StringComparer.Ordinal);

        // Clamped rather than trusted, for the same reason as UiScale above: these are value types
        // in a hand-editable file, so a config written before they existed deserialises them to 0 —
        // which would mean permanent silence and a popup that vanishes before it can be read.
        SoundVolume  = Math.Clamp(SoundVolume, 0f, 1f);
        PopupSeconds = PopupSeconds <= 0f ? 5f : Math.Clamp(PopupSeconds, 2f, 15f);

        // A 0 here — from a hand edit, or from a config written before the field existed —
        // would hide every rated challenge with nothing on screen explaining why.
        MaxDifficulty = Math.Clamp(MaxDifficulty == 0 ? 5 : MaxDifficulty, 1, 5);

        bool changed = false;

        // (1) Give pre-GUID authored challenges a permanent identity, remembering the old id so
        //     its completion can be carried across.
        var remapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in CustomChallenges)
        {
            if (ChallengeCatalog.IsGuid(c.Id)) continue;

            string oldId = c.Id ?? string.Empty;
            c.Id = ChallengeCatalog.NewId();
            if (!string.IsNullOrEmpty(oldId)) remapped[oldId] = c.Id;

            // Carry any completion already sitting in the store under the old id.
            store.RemapId(oldId, c.Id);
            changed = true;
            Plugin.Log.Information($"[Migrate] challenge \"{c.Title}\": {oldId} -> {c.Id}");
        }

        // (2) Adopt v1 completions, which lived in this config keyed by slug.
        if (Completed.Count > 0)
        {
            // The exact original moment is unrecoverable — v1 stored a bool with no timestamp.
            // Stamping "now" is the honest best available, and it only ever applies to entries
            // the permanent ledger has not already dated.
            DateTime stamp = DateTime.UtcNow;
            int adopted = 0;

            foreach (var kv in Completed)
            {
                if (!kv.Value) continue;

                string id = kv.Key;
                if (ChallengeCatalog.LegacyIdMap.TryGetValue(id, out var builtinGuid)) id = builtinGuid;
                else if (remapped.TryGetValue(id, out var newGuid))                    id = newGuid;
                else if (!ChallengeCatalog.IsGuid(id))                                 continue; // unknown, drop

                store.AdoptLegacy(id, stamp);
                adopted++;
            }

            if (adopted > 0)
            {
                store.SaveBoth();
                Plugin.Log.Information($"[Migrate] adopted {adopted} legacy completion(s) into the GUID stores.");
            }

            Completed.Clear();   // migrated; never read again
            changed = true;
        }

        // (3) Backfill sort numbers for challenges authored before ordering existed.
        foreach (var c in CustomChallenges)
        {
            if (c.SortOrder > 0) continue;
            c.SortOrder = ChallengeCatalog.NextSortOrder(this);
            changed = true;
        }

        if (Version < 2)
        {
            Version = 2;
            changed = true;
        }

        if (changed) StateVersion++;
    }
}
