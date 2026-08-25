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

    public bool IsAreaKind =>
        Kind is ChallengeKind.VisitAreas
             or ChallengeKind.VisitAreasInOrder
             or ChallengeKind.EmoteAtArea
             or ChallengeKind.MountInArea
             or ChallengeKind.GearInArea;

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
        _                               => false,
    };
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
