using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Plugin.Services;

namespace TieriChallengesFFXIV;

/// <summary>A challenge that just auto-completed, handed to the toast.</summary>
public sealed class CompletionEvent
{
    public string Title  { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public int    Number { get; init; }

    /// <summary>
    /// Preview-only. When set, the corresponding line is a stand-in for a field the author has
    /// not filled in yet and is rendered in red. Never set for a real completion — a real one
    /// cannot have blank fields, because the creator will not save a challenge without them.
    /// </summary>
    public bool TitleMissing  { get; init; }
    public bool DetailMissing { get; init; }
}

/// <summary>
/// One step of a multi-step challenge just landed — e.g. the second of four areas was reached.
///
/// <para>Raised ONLY for partial progress. The step that finishes a challenge raises
/// <see cref="CompletionEvent"/> instead, so a completion never announces itself twice.</para>
///
/// <para>Carries <see cref="Category"/> as well as <see cref="Id"/> because the notification
/// offers to reveal the challenge in the main window, and finding it means selecting its
/// category first.</para>
/// </summary>
public sealed class ProgressEvent
{
    public string Id       { get; init; } = string.Empty;
    public string Title    { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public int    Number   { get; init; }
    public int    Done     { get; init; }
    public int    Total    { get; init; }
}

/// <summary>
/// Evaluates auto-tracked challenges and marks them complete. Challenges are never completed by
/// the user — this class is the only thing that writes completion.
///
/// <para><b>Cost control.</b> This runs on every framework tick, so the ordering of the
/// early-outs is the design, not an afterthought:</para>
/// <list type="number">
///   <item>Not logged in → return. One bool.</item>
///   <item>Throttle to <see cref="TickIntervalMs"/> (5 Hz). Area membership does not need
///         60 Hz — a player cannot cross a 3-yalm trigger in under 200 ms on foot or mount.</item>
///   <item><b>The active set is precomputed.</b> It holds only challenges that are (a) in the
///         CURRENT territory, (b) not already complete, and (c) well-formed. It is rebuilt only
///         when the territory changes or the config's StateVersion moves — never per tick.</item>
///   <item>Empty active set → return. One int compare. This is the common case: stand in a zone
///         with no challenges and the tracker costs a comparison every 200 ms.</item>
///   <item>Expensive reads (equipment, the outfit index walk, emote resolution) are lazy and
///         happen at most once per tick, and only after a cheap position test has already
///         passed. Standing outside the volume never touches the inventory.</item>
/// </list>
/// </summary>
internal sealed class ChallengeTracker : IDisposable
{
    /// <summary>5 Hz. Fast enough that walking through a small volume cannot be missed.</summary>
    private const int TickIntervalMs = 200;

    public event Action<CompletionEvent>? Completed;

    /// <summary>Partial progress only — the final step raises <see cref="Completed"/> instead.</summary>
    public event Action<ProgressEvent>? Progressed;

    private readonly Configuration   _config;
    private readonly CompletionStore _store;
    private readonly Action          _save;

    /// <summary>Challenges eligible RIGHT NOW, in this zone. Rebuilt only on invalidation.</summary>
    private readonly List<CustomChallenge> _active = new();

    private ushort _cachedTerritory = ushort.MaxValue;
    private int    _cachedVersion   = int.MinValue;
    private long   _lastTickMs;

    private bool _wasLoggedIn;

    /// <summary>
    /// Player position at the previous tick, so movement can be tested as a swept segment rather
    /// than a point sample. Cleared on session change so a stale position cannot be swept from.
    /// </summary>
    private Vector3? _lastPos;

    // Session-scoped progress. "Within one login session" means these are deliberately NOT
    // persisted — they are cleared whenever the character changes or the player logs out.
    private readonly Dictionary<string, HashSet<int>> _visited = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int>          _sequence = new(StringComparer.Ordinal);

    public ChallengeTracker(Configuration config, CompletionStore store, Action save)
    {
        _config = config;
        _store  = store;
        _save   = save;
    }

    public void Attach()    => Plugin.Framework.Update += OnFrameworkUpdate;
    public void Dispose()   => Plugin.Framework.Update -= OnFrameworkUpdate;

    /// <summary>Force a rebuild of the active set — called when definitions change.</summary>
    public void Invalidate() => _cachedVersion = int.MinValue;

    // ── Progress queries for the UI ──────────────────────────────────────────

    /// <summary>
    /// Progress for a multi-step challenge, e.g. 2 of 4 areas visited. Returns false for kinds
    /// that are simply on/off.
    /// </summary>
    public bool TryGetProgress(CustomChallenge ch, out int done, out int total)
    {
        done = 0;
        total = 0;

        switch (ch.Kind)
        {
            case ChallengeKind.VisitAreas:
                total = ch.Areas.Count;
                done  = _visited.TryGetValue(ch.Id, out var set) ? set.Count : 0;
                return total > 0;

            case ChallengeKind.VisitAreasInOrder:
                total = ch.Areas.Count;
                done  = _sequence.TryGetValue(ch.Id, out var idx) ? idx : 0;
                return total > 0;

            default:
                return false;
        }
    }

    // ── Tick ─────────────────────────────────────────────────────────────────

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            // (0) Banned characters track nothing. Ahead of every other gate so a banned session
            // costs one bool read per tick, and so no completion can be recorded while banned —
            // which would otherwise let someone bank progress they are not supposed to be earning.
            if (BanService.IsBanned) return;

            // (1) Cheapest possible gate.
            if (!Plugin.ClientState.IsLoggedIn)
            {
                if (_wasLoggedIn) ClearSession();
                _wasLoggedIn = false;
                return;
            }

            // Session boundary. "Within one login session" is exactly the logged-out → logged-in
            // transition: switching character always passes through a logout, so this single
            // edge covers both a fresh login and a character swap.
            if (!_wasLoggedIn)
            {
                ClearSession();
                _wasLoggedIn = true;
            }

            // (2) Throttle.
            long now = Environment.TickCount64;
            if (now - _lastTickMs < TickIntervalMs) return;
            _lastTickMs = now;

            // (3) Rebuild the active set only when something that affects it changed.
            ushort territory = (ushort)Plugin.ClientState.TerritoryType;
            if (territory != _cachedTerritory || _config.StateVersion != _cachedVersion)
            {
                if (territory != _cachedTerritory)
                {
                    // Changing zone teleports the player as far as coordinates are concerned; a
                    // sweep across that gap would be meaningless.
                    _lastPos = null;

                    // Spoiler-mask unlocking. Deliberately NOT gated behind "does this zone have
                    // any challenges" the way the rest of this method is — a player wandering
                    // through a zone with zero authored content still needs it to stop reading
                    // "??? (unexplored)". Cheap: a HashSet lookup, and this branch already only
                    // runs on an actual zone change, not every tick.
                    if (AttunementService.RecordVisit(_config, territory)) _save();
                }
                Rebuild(territory);
            }

            // (4) Nothing to do in this zone.
            if (_active.Count == 0) return;

            Evaluate();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "ChallengeTracker tick failed");
        }
    }

    private void ClearSession()
    {
        _visited.Clear();
        _sequence.Clear();
        _lastPos = null;
    }

    /// <summary>
    /// Select the challenges worth evaluating in this territory. Everything filtered out here is
    /// work the tick loop never does again until something changes.
    /// </summary>
    private void Rebuild(ushort territory)
    {
        _cachedTerritory = territory;
        _cachedVersion   = _config.StateVersion;

        _active.Clear();

        // Official (synced) challenges as well as locally authored ones.
        foreach (var ch in ChallengeCatalog.AllTrackable(_config))
        {
            if (ch.Kind == ChallengeKind.Manual) continue;      // no detector, never fires
            if (_store.IsComplete(ch.Id))        continue;      // already done
            if (!ch.IsWellFormed())              continue;      // half-authored
            if (ch.TerritoryId != 0 && ch.TerritoryId != territory) continue;  // wrong zone

            _active.Add(ch);
        }

        if (_active.Count > 0)
            Plugin.Log.Debug($"[Tracker] {_active.Count} challenge(s) active in territory {territory}.");
    }

    private void Evaluate()
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return;

        Vector3 pos      = lp.Position;
        float   rotation = lp.Rotation;

        // Lazy expensive state — read at most once per tick, and only when a challenge that has
        // already passed its position gate actually asks for it.
        EquippedSlot[]? equipment = null;
        uint emoteId = 0; bool emoteRead = false;
        uint mountId = 0; bool mountRead = false;

        // Reverse iteration so a completed challenge can be pulled out without disturbing
        // the indices we have not visited yet.
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var ch = _active[i];
            bool done;

            switch (ch.Kind)
            {
                case ChallengeKind.VisitAreas:
                    done = EvalVisitAny(ch, pos);
                    break;

                case ChallengeKind.VisitAreasInOrder:
                    done = EvalVisitOrdered(ch, pos);
                    break;

                case ChallengeKind.EmoteAtArea:
                    done = false;
                    if (ch.Areas[0].Contains(pos)
                        && (!ch.RequireFacing
                            || Facing.AbsDelta(rotation, ch.FacingRadians)
                                   <= Facing.ToRadians(ch.FacingToleranceDeg)))
                    {
                        if (!emoteRead) { emoteId = PlayerStateReader.CurrentEmoteId(); emoteRead = true; }
                        done = emoteId != 0 && emoteId == ch.EmoteId;
                    }
                    break;

                case ChallengeKind.MountInArea:
                    done = false;
                    if (ch.Areas[0].Contains(pos))
                    {
                        if (!mountRead) { mountId = PlayerStateReader.CurrentMountId(); mountRead = true; }
                        done = mountId != 0 && mountId == ch.MountId;
                    }
                    break;

                case ChallengeKind.GearInArea:
                    done = false;
                    if (ch.WholeZone || AnyAreaContains(ch, pos))
                    {
                        equipment ??= PlayerStateReader.ReadEquipment();
                        if (equipment.Length >= PlayerStateReader.EquipSlotCount)
                        {
                            done = ch.GearMode == GearRequirement.FullOutfit
                                 ? PlayerStateReader.IsWearingOutfit(ch.OutfitSetId, equipment)
                                 : WearsItem(equipment, ch.GearItemId);
                        }
                    }
                    break;

                default:
                    done = false;
                    break;
            }

            if (done)
            {
                _active.RemoveAt(i);
                MarkComplete(ch);
            }
        }

        // Recorded last, after every challenge has had a chance to sweep from it.
        _lastPos = pos;
    }

    /// <summary>
    /// Did the player pass through this volume since the last tick?
    ///
    /// <para>A plain point test at 5 Hz misses small volumes: running covers roughly 1.2 yalms
    /// between ticks and mounted is about double that, so anything narrower than that can be
    /// stepped clean over with no sample ever landing inside. Real challenges authored with
    /// half-yalm spheres were effectively untriggerable.</para>
    ///
    /// <para>So the movement since the last tick is treated as a segment and sampled along its
    /// length, finely enough that no volume can fit between two samples. The sample count is
    /// bounded — a teleport or zone change produces a huge delta, and walking that segment is
    /// both pointless and expensive.</para>
    /// </summary>
    private static bool PassedThrough(ChallengeArea area, Vector3 from, Vector3 to)
    {
        if (area.Contains(to)) return true;          // fast path: standing in it right now

        float distance = Vector3.Distance(from, to);
        if (distance <= 0.01f) return false;

        // Step no larger than half the volume's narrowest dimension, so it cannot be skipped.
        float step  = MathF.Max(0.15f, area.MinExtent * 0.5f);
        int   steps = (int)MathF.Ceiling(distance / step);

        // Beyond this the delta is a teleport or a zone change, not walking.
        if (steps > MaxSweepSamples) return false;

        for (int i = 1; i < steps; i++)
        {
            if (area.Contains(Vector3.Lerp(from, to, i / (float)steps))) return true;
        }
        return false;
    }

    /// <summary>Cap on sweep samples per area per tick; above this the movement was not walking.</summary>
    private const int MaxSweepSamples = 24;

    private bool EvalVisitAny(CustomChallenge ch, Vector3 pos)
    {
        if (!_visited.TryGetValue(ch.Id, out var set))
        {
            set = new HashSet<int>();
            _visited[ch.Id] = set;
        }

        Vector3 from = _lastPos ?? pos;
        int before = set.Count;

        for (int a = 0; a < ch.Areas.Count; a++)
        {
            if (set.Contains(a)) continue;
            if (PassedThrough(ch.Areas[a], from, pos)) set.Add(a);
        }

        bool complete = set.Count >= ch.Areas.Count;

        // Announce a step only when it did NOT finish the challenge — the finishing step is
        // announced by MarkComplete, and firing both would double up the sound and the popup.
        if (set.Count > before && !complete)
            RaiseProgress(ch, set.Count, ch.Areas.Count);

        return complete;
    }

    private bool EvalVisitOrdered(CustomChallenge ch, Vector3 pos)
    {
        int idx = _sequence.TryGetValue(ch.Id, out var v) ? v : 0;
        Vector3 from = _lastPos ?? pos;

        // Only the NEXT area in the sequence is tested — entering a later one early does
        // nothing, which is what "in order" means. Swept for the same reason as VisitAny:
        // a small volume must not be skippable by running past it.
        bool advanced = false;
        if (idx < ch.Areas.Count && PassedThrough(ch.Areas[idx], from, pos))
        {
            idx++;
            _sequence[ch.Id] = idx;
            advanced = true;
        }

        bool complete = idx >= ch.Areas.Count;

        // Same rule as VisitAny: the finishing step belongs to MarkComplete, not here.
        if (advanced && !complete)
            RaiseProgress(ch, idx, ch.Areas.Count);

        return complete;
    }

    /// <summary>
    /// Announce partial progress. Never throws into the tick loop — a subscriber blowing up must
    /// not cost the player the progress that was just recorded.
    /// </summary>
    private void RaiseProgress(CustomChallenge ch, int done, int total)
    {
        int number = ChallengeCatalog.DisplayNumber(_config, ch.Id);
        Plugin.Log.Debug($"[Tracker] Challenge #{number} \"{ch.Title}\" progressed to {done}/{total}.");

        try
        {
            Progressed?.Invoke(new ProgressEvent
            {
                Id       = ch.Id,
                Title    = ch.Title,
                Category = ch.Category,
                Number   = number,
                Done     = done,
                Total    = total,
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Progress handler threw");
        }
    }

    private static bool AnyAreaContains(CustomChallenge ch, Vector3 pos)
    {
        for (int a = 0; a < ch.Areas.Count; a++)
            if (ch.Areas[a].Contains(pos)) return true;
        return false;
    }

    private static bool WearsItem(EquippedSlot[] equipment, uint itemId)
    {
        if (itemId == 0) return false;
        for (int i = 0; i < equipment.Length; i++)
            if (equipment[i].ItemId == itemId || equipment[i].GlamourId == itemId) return true;
        return false;
    }

    /// <summary>
    /// The single place completion is written. Bumps StateVersion, which invalidates the active
    /// set on the next tick, so a completed challenge is never evaluated again.
    /// </summary>
    private void MarkComplete(CustomChallenge ch)
    {
        // Writes current AND appends to the permanent ledger (permanent keeps the earliest date
        // and is never overwritten). Both files are flushed here, not deferred — a completion
        // that only exists in memory is a completion the user can lose to a crash.
        _store.MarkComplete(ch.Id);
        _config.StateVersion++;   // invalidates the active set so this is never evaluated again

        _visited.Remove(ch.Id);
        _sequence.Remove(ch.Id);

        int number = ChallengeCatalog.DisplayNumber(_config, ch.Id);
        Plugin.Log.Information($"[Tracker] Challenge #{number} \"{ch.Title}\" auto-completed.");

        try
        {
            Completed?.Invoke(new CompletionEvent
            {
                Title  = ch.Title,
                Detail = ch.Detail,
                Number = number,
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Completion handler threw");
        }
    }
}
