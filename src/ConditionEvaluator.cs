using System;
using System.Numerics;

using Dalamud.Game.ClientState.Conditions;

namespace TieriChallengesFFXIV;

/// <summary>
/// Everything about the player that a condition might ask for this tick, read at most once each
/// and shared by every challenge being evaluated.
///
/// <para><b>This is the class that keeps the promise in <see cref="ChallengeTracker"/>'s header.</b>
/// Twelve challenges asking "am I wearing the Ala Mhigan set" must produce ONE equipment read, not
/// twelve, and a challenge whose position test failed must produce none at all. Every accessor here
/// is lazy and latched; <see cref="Begin"/> clears the latches for the next tick.</para>
///
/// <para>Reused rather than reallocated per tick — it is touched five times a second forever, and
/// the equipment array it holds is the only sizeable thing in it.</para>
/// </summary>
internal sealed class TickState
{
    public Vector3 Pos      { get; private set; }
    public float   Rotation { get; private set; }

    private uint _emote;    private bool _emoteRead;
    private uint _mount;    private bool _mountRead;
    private uint _minion;   private bool _minionRead;
    private uint _target;   private bool _targetRead;
    private uint _job;      private bool _jobRead;
    private int  _level;    private bool _levelRead;
    private int  _hour;     private bool _hourRead;

    private EquippedSlot[]? _equipment;

    /// <summary>Start a new tick. Clears every latch so the next read goes back to the game.</summary>
    public void Begin(Vector3 pos, float rotation)
    {
        Pos      = pos;
        Rotation = rotation;

        _emoteRead = _mountRead = _minionRead = _targetRead = false;
        _jobRead   = _levelRead = _hourRead   = false;
        _equipment = null;
    }

    public uint Emote  { get { if (!_emoteRead)  { _emote  = PlayerStateReader.CurrentEmoteId();     _emoteRead  = true; } return _emote;  } }
    public uint Mount  { get { if (!_mountRead)  { _mount  = PlayerStateReader.CurrentMountId();     _mountRead  = true; } return _mount;  } }
    public uint Minion { get { if (!_minionRead) { _minion = PlayerStateReader.CurrentMinionId();    _minionRead = true; } return _minion; } }
    public uint Target { get { if (!_targetRead) { _target = PlayerStateReader.CurrentTargetDataId(); _targetRead = true; } return _target; } }
    public uint Job    { get { if (!_jobRead)    { _job    = PlayerStateReader.CurrentJobId();       _jobRead    = true; } return _job;    } }
    public int  Level  { get { if (!_levelRead)  { _level  = PlayerStateReader.CurrentLevel();       _levelRead  = true; } return _level;  } }
    public int  Hour   { get { if (!_hourRead)   { _hour   = PlayerStateReader.EorzeaHour();         _hourRead   = true; } return _hour;   } }

    /// <summary>
    /// The equipped container. THE expensive read — everything else above is a field poke or a
    /// dictionary hit. Nothing should reach this without having passed a position test first.
    /// </summary>
    public EquippedSlot[] Equipment => _equipment ??= PlayerStateReader.ReadEquipment();
}

/// <summary>
/// Decides whether a single <see cref="ChallengeCondition"/> currently holds.
///
/// <para>Static and stateless: everything mutable lives in the <see cref="TickState"/> handed in,
/// which is what lets the creator preview and the dev status block evaluate a condition using the
/// same code the tracker runs, without either of them owning tracker state.</para>
/// </summary>
internal static class ConditionEvaluator
{
    /// <summary>
    /// Do ALL of a stop's conditions hold right now?
    ///
    /// <para><b>Two passes, cheap first.</b> Every condition except the two gear ones costs a field
    /// read or a dictionary lookup; the gear ones walk the equipped container and, for an outfit,
    /// the MirageStoreSetItem index. Running the cheap ones first means "wearing the full Ala Mhigan
    /// set while riding a Fat Chocobo" never reads equipment unless the player is actually on the
    /// chocobo. Ordering by cost rather than by author order costs nothing — conditions are ANDed,
    /// so their order is not observable.</para>
    /// </summary>
    public static bool AllHold(AreaRequirement req, TickState s)
    {
        var list = req.Conditions;
        if (list == null || list.Count == 0) return true;   // presence-only stop

        for (int i = 0; i < list.Count; i++)
            if (!list[i].IsExpensive && !Holds(list[i], s)) return false;

        for (int i = 0; i < list.Count; i++)
            if (list[i].IsExpensive && !Holds(list[i], s)) return false;

        return true;
    }

    /// <summary>
    /// One condition, with <see cref="ChallengeCondition.Negate"/> applied.
    /// Presence ignores Negate — "be in this area but not in it" is not a thing.
    /// </summary>
    /// <remarks>
    /// <para><b>"Could not evaluate" is not the same as "evaluated false", and Negate is why.</b>
    /// <see cref="Raw"/> returns null for a condition this build cannot judge — an unknown
    /// <see cref="ConditionType"/>, a <see cref="GameStateFlag"/> with no mapping, or an exception
    /// out of the game read. Collapsing that to false looks like failing closed and is the exact
    /// opposite for a negated condition: "NOT mounted" against an unreadable mount state would
    /// report satisfied and hand out a completion nobody earned. Null short-circuits ahead of
    /// Negate so an unjudgeable condition can only ever block.</para>
    /// </remarks>
    public static bool Holds(ChallengeCondition c, TickState s)
    {
        bool? raw = Raw(c, s);
        if (raw == null) return false;

        if (c.Type == ConditionType.Presence) return raw.Value;
        return c.Negate ? !raw.Value : raw.Value;
    }

    /// <summary>The condition's own truth, or null when this build cannot judge it at all.</summary>
    private static bool? Raw(ChallengeCondition c, TickState s)
    {
        try
        {
            switch (c.Type)
            {
                case ConditionType.Presence:
                    return true;   // the caller already established we are inside the area

                case ConditionType.Emote:
                    return c.EmoteId != 0 && s.Emote == c.EmoteId;

                case ConditionType.Mount:
                    return c.MountId != 0 && s.Mount == c.MountId;

                case ConditionType.Minion:
                    return c.MinionId != 0 && s.Minion == c.MinionId;

                case ConditionType.Target:
                    return c.TargetDataId != 0 && s.Target == c.TargetDataId;

                case ConditionType.Facing:
                    return Facing.AbsDelta(s.Rotation, c.FacingRadians)
                           <= Facing.ToRadians(c.FacingToleranceDeg);

                case ConditionType.GameState:
                {
                    // ToFlag answers None for a GameStateFlag this build has no mapping for.
                    // Condition[None] is just an array read that reports false, which is a verdict
                    // rather than the abstention this actually is.
                    var flag = ToFlag(c.Flag);
                    if (flag == ConditionFlag.None) return null;
                    return Plugin.Condition[flag];
                }

                case ConditionType.Job:
                    if (c.JobId == 0 || s.Job != c.JobId) return false;
                    return c.MaxLevel <= 0 || s.Level <= c.MaxLevel;

                case ConditionType.TimeOfDay:
                    return InTimeWindow(s.Hour, c.StartHour, c.EndHour);

                case ConditionType.HasItem:
                    return Plugin.Inventory.Has(c.ItemId, c.ItemCount);

                case ConditionType.FullOutfit:
                {
                    var eq = s.Equipment;
                    if (eq.Length < PlayerStateReader.EquipSlotCount) return false;
                    return PlayerStateReader.IsWearingOutfit(c.OutfitSetId, eq);
                }

                case ConditionType.GearPieces:
                    return WearsPieces(c, s);

                default:
                    // An unknown type reaching here means a challenge slipped past the version
                    // gate. Abstain rather than answer: never completing is recoverable, wrongly
                    // completing is not.
                    return null;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[Condition] {c.Type} threw, treated as unjudgeable: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Is the Eorzean hour inside [start, end)? Wraps past midnight, so 22→4 is a night window
    /// rather than an empty one.
    /// </summary>
    private static bool InTimeWindow(int hour, int startHour, int endHour)
    {
        if (hour < 0) return false;                 // clock unreadable — fail closed

        int start = ((startHour % 24) + 24) % 24;

        // 24 is how "up to midnight" is authored; it is the same instant as 0 but must not collapse
        // the window to nothing when paired with a start of 0.
        int end = endHour == 24 ? 0 : ((endHour % 24) + 24) % 24;

        if (start == end) return true;              // full day
        return start < end ? hour >= start && hour < end
                           : hour >= start || hour < end;
    }

    private static bool WearsPieces(ChallengeCondition c, TickState s)
    {
        if (c.Pieces == null || c.Pieces.Count == 0) return false;

        var eq = s.Equipment;
        if (eq.Length < PlayerStateReader.EquipSlotCount) return false;

        int matched = 0;
        foreach (var p in c.Pieces)
        {
            if (p.ItemId == 0) continue;
            if (p.Slot < 0 || p.Slot >= eq.Length) continue;

            // VisibleId, so a glamoured piece counts — the challenge is about what the player is
            // seen wearing, which is the same rule IsWearingOutfit follows.
            if (eq[p.Slot].VisibleId == p.ItemId) matched++;
        }

        return matched >= c.EffectiveRequiredCount;
    }

    /// <summary>
    /// The ONE place this plugin's stable <see cref="GameStateFlag"/> meets Dalamud's game-derived
    /// <c>ConditionFlag</c>. See the type's own remarks for why the indirection exists.
    /// </summary>
    public static ConditionFlag ToFlag(GameStateFlag f) => f switch
    {
        GameStateFlag.Mounted         => ConditionFlag.Mounted,
        GameStateFlag.Swimming        => ConditionFlag.Swimming,
        GameStateFlag.Diving          => ConditionFlag.Diving,
        GameStateFlag.Flying          => ConditionFlag.InFlight,
        GameStateFlag.InCombat        => ConditionFlag.InCombat,
        GameStateFlag.Crafting        => ConditionFlag.Crafting,
        GameStateFlag.Gathering       => ConditionFlag.Gathering,
        GameStateFlag.Fishing         => ConditionFlag.Fishing,
        GameStateFlag.Jumping         => ConditionFlag.Jumping,
        GameStateFlag.Emoting         => ConditionFlag.Emoting,
        GameStateFlag.Performing      => ConditionFlag.Performing,
        GameStateFlag.BoundByDuty     => ConditionFlag.BoundByDuty,
        GameStateFlag.Stealthed       => ConditionFlag.Stealthed,
        GameStateFlag.RidingPillion   => ConditionFlag.RidingPillion,
        GameStateFlag.InDeepDungeon   => ConditionFlag.InDeepDungeon,
        GameStateFlag.ChocoboRacing   => ConditionFlag.ChocoboRacing,
        GameStateFlag.Transformed     => ConditionFlag.Transformed,
        GameStateFlag.PlayingMiniGame => ConditionFlag.PlayingMiniGame,
        _                             => ConditionFlag.None,
    };

    /// <summary>
    /// Live truth for one condition, for the creator's "is this true right now?" readout. Builds
    /// its own single-use TickState — never call this from the tracker, which shares one.
    /// </summary>
    public static bool HoldsNow(ChallengeCondition c)
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return false;

        var s = new TickState();
        s.Begin(lp.Position, lp.Rotation);
        return Holds(c, s);
    }
}
