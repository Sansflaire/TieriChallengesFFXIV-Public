using System;
using System.Collections.Generic;

namespace TieriChallengesFFXIV;

/// <summary>
/// How many areas a composite challenge has, and whether their order matters.
/// Persisted as an int — append, never renumber.
/// </summary>
public enum AreaMode
{
    /// <summary>Exactly one area. The whole challenge is that area's condition set.</summary>
    Single = 0,

    /// <summary>Several areas, satisfied in any order, within one login session.</summary>
    AnyOrder = 1,

    /// <summary>Several areas, satisfied in the listed order. A later one entered early does nothing.</summary>
    InOrder = 2,
}

/// <summary>
/// What a single condition tests. Persisted as an int — <b>append, never renumber</b>: a saved
/// challenge stores the raw value, and shifting these would silently reinterpret every published
/// condition as a different one.
///
/// <para>This list is meant to grow. Adding a member needs four things and nothing else: a case in
/// <see cref="ConditionEvaluator.Holds"/>, a case in <see cref="ChallengeCondition.Describe"/>, an
/// editor block in the Creator, and a well-formed rule in <see cref="ChallengeCondition.IsWellFormed"/>.
/// Nothing in the tracker or the UI switches on the full set.</para>
/// </summary>
public enum ConditionType
{
    /// <summary>Just be inside the area. The degenerate condition — this is the old VisitAreas.</summary>
    Presence = 0,

    /// <summary>Performing a specific emote.</summary>
    Emote = 1,

    /// <summary>Riding a specific mount.</summary>
    Mount = 2,

    /// <summary>A specific minion is summoned.</summary>
    Minion = 3,

    /// <summary>Wearing every defined slot of a Glamour Dresser outfit.</summary>
    FullOutfit = 4,

    /// <summary>Wearing specific items in specific slots — N of the M listed.</summary>
    GearPieces = 5,

    /// <summary>Targeting a specific NPC, identified by DataId so it works on any instance of it.</summary>
    Target = 6,

    /// <summary>Facing a captured direction, within a tolerance arc. Modifier only — see IsWellFormed.</summary>
    Facing = 7,

    /// <summary>A game state flag: mounted, swimming, in combat, crafting, and so on.</summary>
    GameState = 8,

    /// <summary>Playing a specific job, optionally at or below a level.</summary>
    Job = 9,

    /// <summary>Within a window of the Eorzean clock. Wraps past midnight.</summary>
    TimeOfDay = 10,

    /// <summary>Holding at least N of an item. Event-driven — never walks the bags.</summary>
    HasItem = 11,
}

/// <summary>
/// A curated subset of Dalamud's <c>ConditionFlag</c>, worth authoring against.
///
/// <para><b>This is deliberately its own enum rather than persisting <c>ConditionFlag</c> directly.</b>
/// Those values are game-derived and have been renumbered by SE across patches; a published
/// challenge storing the raw flag would quietly start testing something else after a patch.
/// <see cref="ConditionEvaluator.ToFlag"/> is the one place the mapping lives, so a renumber costs
/// one edit there instead of invalidating everyone's saved challenges.</para>
/// </summary>
public enum GameStateFlag
{
    Mounted        = 0,
    Swimming       = 1,
    Diving         = 2,
    Flying         = 3,
    InCombat       = 4,
    Crafting       = 5,
    Gathering      = 6,
    Fishing        = 7,
    Jumping        = 8,
    Emoting        = 9,
    Performing     = 10,
    BoundByDuty    = 11,
    Stealthed      = 12,
    RidingPillion  = 13,
    InDeepDungeon  = 14,
    ChocoboRacing  = 15,
    Transformed    = 16,
    PlayingMiniGame = 17,
}

/// <summary>One slot of a <see cref="ConditionType.GearPieces"/> requirement.</summary>
[Serializable]
public sealed class GearPiece
{
    /// <summary>Index into <see cref="PlayerStateReader.SlotNames"/> (EquippedItems order).</summary>
    public int Slot { get; set; }

    public uint   ItemId   { get; set; }
    public string ItemName { get; set; } = string.Empty;

    public GearPiece Clone() => new() { Slot = Slot, ItemId = ItemId, ItemName = ItemName };
}

/// <summary>
/// One thing that must be true while the player is inside an area.
///
/// <para>Shaped as a discriminated record with a superset of fields rather than a class hierarchy,
/// matching how <see cref="CustomChallenge"/> already stores its per-kind data. Newtonsoft handles
/// it with no converter, absent properties deserialise to their defaults, and an older build
/// reading a newer condition type simply fails <see cref="IsWellFormed"/> — which is the safe
/// direction, because the version gate withholds the challenge entirely before it gets that far.</para>
/// </summary>
[Serializable]
public sealed class ChallengeCondition
{
    public ConditionType Type { get; set; } = ConditionType.Presence;

    /// <summary>
    /// Invert the test — "while NOT mounted", "while NOT targeting the Sultana". Applies to every
    /// type except <see cref="ConditionType.Presence"/>, where it would contradict the area itself.
    /// </summary>
    public bool Negate { get; set; }

    // ── Emote ────────────────────────────────────────────────────────────────
    public uint   EmoteId   { get; set; }
    public string EmoteName { get; set; } = string.Empty;

    // ── Mount ────────────────────────────────────────────────────────────────
    public uint   MountId   { get; set; }
    public string MountName { get; set; } = string.Empty;

    // ── Minion ───────────────────────────────────────────────────────────────
    public uint   MinionId   { get; set; }
    public string MinionName { get; set; } = string.Empty;

    // ── FullOutfit ───────────────────────────────────────────────────────────
    public uint   OutfitSetId { get; set; }
    public string OutfitName  { get; set; } = string.Empty;

    // ── GearPieces ───────────────────────────────────────────────────────────
    public List<GearPiece> Pieces { get; set; } = new();

    /// <summary>
    /// How many of <see cref="Pieces"/> must match. 0 means "all of them", which is what an author
    /// means by default — listing four pieces and requiring four is the common case, and a 0 from
    /// a config written before this field existed deserialises to exactly that.
    /// </summary>
    public int RequiredCount { get; set; }

    // ── Target ───────────────────────────────────────────────────────────────
    /// <summary>NPC <c>DataId</c>, not <c>EntityId</c> — the latter is per-spawn and useless across sessions.</summary>
    public uint   TargetDataId { get; set; }
    public string TargetName   { get; set; } = string.Empty;

    // ── Facing ───────────────────────────────────────────────────────────────
    public float FacingRadians      { get; set; }
    public float FacingToleranceDeg { get; set; } = 30f;

    // ── GameState ────────────────────────────────────────────────────────────
    public GameStateFlag Flag { get; set; }

    // ── Job ──────────────────────────────────────────────────────────────────
    public uint   JobId   { get; set; }
    public string JobName { get; set; } = string.Empty;

    /// <summary>Level ceiling, 0 = any level. "Do this as a Botanist at level 10 or below."</summary>
    public int MaxLevel { get; set; }

    // ── TimeOfDay ────────────────────────────────────────────────────────────
    /// <summary>Eorzean hour the window opens, 0–23.</summary>
    public int StartHour { get; set; }

    /// <summary>Eorzean hour the window closes, 0–23, exclusive. Wraps: 22→4 is a night window.</summary>
    public int EndHour { get; set; } = 24;

    // ── HasItem ──────────────────────────────────────────────────────────────
    public uint   ItemId   { get; set; }
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Minimum quantity. 0 and 1 both mean "at least one".</summary>
    public int ItemCount { get; set; } = 1;

    /// <summary>
    /// Reading equipment is the only genuinely expensive condition — it walks the equipped
    /// container and, for an outfit, the whole MirageStoreSetItem index. The evaluator runs every
    /// cheap condition first and only touches these if the cheap ones all passed, which preserves
    /// the tracker's standing promise that standing outside a volume never reads the inventory.
    /// </summary>
    public bool IsExpensive =>
        Type is ConditionType.FullOutfit or ConditionType.GearPieces;

    /// <summary>
    /// Everything this condition needs to be evaluable is present.
    ///
    /// <para><see cref="ConditionType.Facing"/> is special: it is a MODIFIER, never a requirement
    /// on its own. Character rotation sweeps through any given arc constantly while a player mills
    /// about, so a facing-only area would fire the instant they happened to turn — it has to
    /// qualify something else. <see cref="AreaRequirement.IsWellFormed"/> enforces that.</para>
    /// </summary>
    public bool IsWellFormed() => Type switch
    {
        ConditionType.Presence   => true,
        ConditionType.Emote      => EmoteId != 0,
        ConditionType.Mount      => MountId != 0,
        ConditionType.Minion     => MinionId != 0,
        ConditionType.FullOutfit => OutfitSetId != 0,
        ConditionType.GearPieces => HasUsablePieces(),
        ConditionType.Target     => TargetDataId != 0,
        ConditionType.Facing     => FacingToleranceDeg > 0f,
        ConditionType.GameState  => true,
        ConditionType.Job        => JobId != 0,
        ConditionType.TimeOfDay  => StartHour is >= 0 and <= 23
                                 && EndHour   is >= 0 and <= 24
                                 && StartHour != EndHour,
        ConditionType.HasItem    => ItemId != 0,
        _                        => false,
    };

    private bool HasUsablePieces()
    {
        if (Pieces == null || Pieces.Count == 0) return false;
        foreach (var p in Pieces) if (p.ItemId == 0) return false;
        return RequiredCount <= Pieces.Count;
    }

    /// <summary>How many pieces actually have to match — 0 in the data means "all of them".</summary>
    public int EffectiveRequiredCount =>
        RequiredCount > 0 ? Math.Min(RequiredCount, Pieces?.Count ?? 0) : (Pieces?.Count ?? 0);

    /// <summary>One line for the creator list and the objective readout.</summary>
    public string Describe()
    {
        string not = Negate ? "not " : string.Empty;

        return Type switch
        {
            ConditionType.Presence   => "be here",
            ConditionType.Emote      => $"{not}performing /{Named(EmoteName, EmoteId)}",
            ConditionType.Mount      => $"{not}riding {Named(MountName, MountId)}",
            ConditionType.Minion     => $"{not}with {Named(MinionName, MinionId)} out",
            ConditionType.FullOutfit => $"{not}wearing the {Named(OutfitName, OutfitSetId)}",
            ConditionType.GearPieces => DescribePieces(not),
            ConditionType.Target     => $"{not}targeting {Named(TargetName, TargetDataId)}",
            ConditionType.Facing     => $"facing {Facing.ToDegrees(FacingRadians):0}° (±{FacingToleranceDeg:0}°)",
            ConditionType.GameState  => $"{not}{FlagLabel(Flag)}",
            ConditionType.Job        => MaxLevel > 0
                                            ? $"{not}as {Named(JobName, JobId)} at level {MaxLevel} or below"
                                            : $"{not}as {Named(JobName, JobId)}",
            ConditionType.TimeOfDay  => $"{not}between {StartHour:00}:00 and {EndHour:00}:00 Eorzean",
            ConditionType.HasItem    => ItemCount > 1
                                            ? $"{not}carrying {ItemCount}× {Named(ItemName, ItemId)}"
                                            : $"{not}carrying {Named(ItemName, ItemId)}",
            _                        => "unknown condition",
        };
    }

    private string DescribePieces(string not)
    {
        int need = EffectiveRequiredCount;
        int have = Pieces?.Count ?? 0;

        if (have == 0) return $"{not}wearing (nothing chosen)";
        if (have == 1) return $"{not}wearing {Named(Pieces![0].ItemName, Pieces[0].ItemId)}";

        return need >= have
            ? $"{not}wearing all {have} chosen pieces"
            : $"{not}wearing any {need} of {have} chosen pieces";
    }

    private static string Named(string name, uint id) =>
        string.IsNullOrWhiteSpace(name) ? $"#{id}" : name;

    public static string FlagLabel(GameStateFlag f) => f switch
    {
        GameStateFlag.Mounted         => "mounted",
        GameStateFlag.Swimming        => "swimming",
        GameStateFlag.Diving          => "diving",
        GameStateFlag.Flying          => "flying",
        GameStateFlag.InCombat        => "in combat",
        GameStateFlag.Crafting        => "crafting",
        GameStateFlag.Gathering       => "gathering",
        GameStateFlag.Fishing         => "fishing",
        GameStateFlag.Jumping         => "jumping",
        GameStateFlag.Emoting         => "emoting",
        GameStateFlag.Performing      => "performing",
        GameStateFlag.BoundByDuty     => "in a duty",
        GameStateFlag.Stealthed       => "stealthed",
        GameStateFlag.RidingPillion   => "riding pillion",
        GameStateFlag.InDeepDungeon   => "in a deep dungeon",
        GameStateFlag.ChocoboRacing   => "chocobo racing",
        GameStateFlag.Transformed     => "transformed",
        GameStateFlag.PlayingMiniGame => "playing a mini-game",
        _                             => f.ToString().ToLowerInvariant(),
    };

    public ChallengeCondition Clone()
    {
        var c = new ChallengeCondition
        {
            Type = Type, Negate = Negate,
            EmoteId = EmoteId, EmoteName = EmoteName,
            MountId = MountId, MountName = MountName,
            MinionId = MinionId, MinionName = MinionName,
            OutfitSetId = OutfitSetId, OutfitName = OutfitName,
            RequiredCount = RequiredCount,
            TargetDataId = TargetDataId, TargetName = TargetName,
            FacingRadians = FacingRadians, FacingToleranceDeg = FacingToleranceDeg,
            Flag = Flag,
            JobId = JobId, JobName = JobName, MaxLevel = MaxLevel,
            StartHour = StartHour, EndHour = EndHour,
            ItemId = ItemId, ItemName = ItemName, ItemCount = ItemCount,
        };

        if (Pieces != null)
            foreach (var p in Pieces) c.Pieces.Add(p.Clone());

        return c;
    }
}

/// <summary>
/// One area plus everything that must be true while standing in it. This is the unit a composite
/// challenge is built from: <see cref="AreaMode.Single"/> has one, the multi modes have several.
///
/// <para>Binding conditions to the AREA rather than to the challenge is what makes
/// "goblin mask here, bear mittens there" expressible at all — the conditions differ per stop.</para>
/// </summary>
[Serializable]
public sealed class AreaRequirement
{
    public ChallengeArea Area { get; set; } = new();

    /// <summary>ALL of these must hold simultaneously while inside <see cref="Area"/>.</summary>
    public List<ChallengeCondition> Conditions { get; set; } = new();

    /// <summary>
    /// <see cref="AreaMode.InOrder"/> only: seconds allowed since the PREVIOUS area was satisfied.
    /// 0 = untimed. This is the "within X seconds of Y" relation — expressed as a budget on the
    /// step rather than as a condition, because it is a relation between two areas and a condition
    /// only ever sees one.
    /// </summary>
    public int WithinSeconds { get; set; }

    /// <summary>
    /// Display name for the objective list. Falls back to the area's own name, which the creator
    /// seeds as "Area 1", "Area 2"…
    /// </summary>
    public string Label { get; set; } = string.Empty;

    public string DisplayLabel =>
        !string.IsNullOrWhiteSpace(Label) ? Label
        : !string.IsNullOrWhiteSpace(Area?.Name) ? Area!.Name
        : "Area";

    /// <summary>
    /// A requirement with no conditions at all is a plain "be here", which is legal and is exactly
    /// what the old VisitAreas kind did. What is NOT legal is a set consisting only of modifiers:
    /// see <see cref="ChallengeCondition.IsWellFormed"/> for why facing alone cannot stand up.
    /// </summary>
    public bool IsWellFormed()
    {
        if (Area == null) return false;
        if (Conditions == null || Conditions.Count == 0) return true;   // presence-only

        bool anyNonModifier = false;
        foreach (var c in Conditions)
        {
            if (!c.IsWellFormed()) return false;
            if (c.Type != ConditionType.Facing) anyNonModifier = true;
        }

        return anyNonModifier;
    }

    /// <summary>One line summarising the whole stop, for the objective list.</summary>
    public string Describe()
    {
        if (Conditions == null || Conditions.Count == 0) return DisplayLabel;

        var parts = new List<string>(Conditions.Count);
        foreach (var c in Conditions) parts.Add(c.Describe());
        return $"{DisplayLabel} — {string.Join(", ", parts)}";
    }

    public AreaRequirement Clone()
    {
        var r = new AreaRequirement
        {
            Area          = Area?.Clone() ?? new ChallengeArea(),
            WithinSeconds = WithinSeconds,
            Label         = Label,
        };

        if (Conditions != null)
            foreach (var c in Conditions) r.Conditions.Add(c.Clone());

        return r;
    }
}
