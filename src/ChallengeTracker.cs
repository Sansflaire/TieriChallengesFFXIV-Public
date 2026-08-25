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

    /// <summary>
    /// When the last ordered step of a composite challenge was satisfied, per challenge id, as a
    /// <see cref="Environment.TickCount64"/> stamp. Only consulted by stops that declare a
    /// <see cref="AreaRequirement.WithinSeconds"/> budget; session-scoped like everything else here.
    /// </summary>
    private readonly Dictionary<string, long> _stepStamp = new(StringComparer.Ordinal);

    /// <summary>
    /// Shared per-tick player state. One instance, reused, so twelve challenges asking the same
    /// question produce one read — see <see cref="TickState"/>.
    /// </summary>
    private readonly TickState _tick = new();

    // ── Race state ───────────────────────────────────────────────────────────

    /// <summary>The one race currently running, or null. See <see cref="RaceRun"/> for why one.</summary>
    private RaceRun? _run;

    /// <summary>
    /// Races whose start volume contains the player RIGHT NOW. Rebuilt every tick and read by the
    /// prompt and the challenge row, so neither has to do any position testing of its own.
    /// </summary>
    private readonly List<string> _armed = new();

    /// <summary>Raised when a run ends for any reason, including a successful finish.</summary>
    public event Action<RaceEndedEvent>? RaceEnded;

    /// <summary>Raised when a run starts or restarts, so the UI can acknowledge the press.</summary>
    public event Action<string>? RaceStarted;

    /// <summary>Races the player could start this instant.</summary>
    public IReadOnlyList<string> ArmedRaces => _armed;

    /// <summary>Id of the running race, or null.</summary>
    public string? RunningRaceId => _run?.Id;

    /// <summary>
    /// Elapsed time of the running race. Computed from the clock on demand rather than sampled by
    /// the 5 Hz tick, so the on-screen timer moves smoothly at frame rate instead of in visible
    /// 200 ms steps.
    /// </summary>
    public double RunningElapsedSeconds => _run?.ElapsedSeconds ?? 0;

    public bool IsRaceRunning(string id) =>
        _run != null && string.Equals(_run.Id, id, StringComparison.OrdinalIgnoreCase);

    public bool IsRaceArmed(string id)
    {
        for (int i = 0; i < _armed.Count; i++)
            if (string.Equals(_armed[i], id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Begin a run. Refuses unless the player is standing in that race's start volume right now —
    /// the arming test is the same one the prompt uses, so the button can never start a race the
    /// player has already walked away from.
    /// </summary>
    public bool TryStartRace(string id)
    {
        if (string.IsNullOrEmpty(id) || !IsRaceArmed(id)) return false;

        // Starting a race while another is running abandons the first. Silently dropping it would
        // leave a clock running for something the player has clearly stopped doing.
        if (_run != null && !string.Equals(_run.Id, id, StringComparison.OrdinalIgnoreCase))
            EndRun(RaceOutcome.Abandoned);

        _run = new RaceRun { Id = id, StartedAtMs = Environment.TickCount64, LeftStart = false };

        Plugin.Log.Information($"[Race] started {id}");
        try { RaceStarted?.Invoke(id); }
        catch (Exception ex) { Plugin.Log.Error(ex, "Race start handler threw"); }

        return true;
    }

    /// <summary>Give up on the running race, if any.</summary>
    public void AbandonRace() => EndRun(RaceOutcome.Abandoned);

    /// <summary>
    /// Close out the running race. Every exit from a run goes through here so the event is raised
    /// exactly once and <see cref="_run"/> is always cleared — a run left dangling would keep
    /// failing its own time limit every tick forever.
    /// </summary>
    private void EndRun(RaceOutcome outcome, bool newBest = false, double? previousBest = null,
                        bool firstCompletion = false)
    {
        if (_run == null) return;

        var run = _run;
        _run = null;   // cleared BEFORE the event, so a handler that starts another race works

        string title = ChallengeCatalog.FindCustom(_config, run.Id)?.Title ?? string.Empty;

        Plugin.Log.Information(
            $"[Race] {run.Id} ended: {outcome} at {run.ElapsedSeconds:0.00}s");

        try
        {
            RaceEnded?.Invoke(new RaceEndedEvent
            {
                Id              = run.Id,
                Title           = title,
                Outcome         = outcome,
                Seconds         = run.ElapsedSeconds,
                NewBest         = newBest,
                PreviousBest    = previousBest,
                FirstCompletion = firstCompletion,
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Race end handler threw");
        }
    }

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

    /// <summary>
    /// Drop every in-memory objective set. Called by Reset alongside
    /// <see cref="ProgressStore.ResetAll"/> — wiping only the file would leave these sets live in
    /// memory, and the next satisfied stop would write the old positions straight back out.
    /// </summary>
    public void ClearPartialProgress()
    {
        _visited.Clear();
        _sequence.Clear();
        _stepStamp.Clear();
    }

    // ── Progress queries for the UI ──────────────────────────────────────────

    /// <summary>
    /// Progress for a multi-step challenge, e.g. 2 of 4 areas visited. Returns false for kinds
    /// that are simply on/off.
    /// </summary>
    public bool TryGetProgress(CustomChallenge ch, out int done, out int total)
    {
        done = 0;
        total = 0;

        // A chain counts STEPS, not the stops inside the step it happens to be on — "3/5" on a
        // quest means three legs done, which is the number the player is tracking.
        if (ch.IsChain)
        {
            total = ch.ChainSteps.Count;
            done  = Math.Clamp(Plugin.Progress.ChainStep(ch.Id), 0, total);
            return total > 1;
        }

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

            case ChallengeKind.InArea:
                // Single is one condition set — "1/1" is noise, so it reports no progress at all,
                // exactly like the other single-condition kinds.
                if (!ChallengeCatalog.HasStepProgress(ch)) return false;

                total = ch.StopCount;
                done  = ch.Mode == AreaMode.InOrder
                    ? (_sequence.TryGetValue(ch.Id, out var cidx) ? cidx : 0)
                    : (_visited.TryGetValue(ch.Id, out var cset) ? cset.Count : 0);
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

            // Cleared here rather than inside Evaluate so it is emptied on EVERY path — including
            // the "no challenges in this zone" bail below. Left stale, the prompt would keep
            // offering a race the player has walked out of, or out of the zone entirely.
            _armed.Clear();

            // (3) Rebuild the active set only when something that affects it changed.
            ushort territory = (ushort)Plugin.ClientState.TerritoryType;
            if (territory != _cachedTerritory || _config.StateVersion != _cachedVersion)
            {
                if (territory != _cachedTerritory)
                {
                    // Changing zone teleports the player as far as coordinates are concerned; a
                    // sweep across that gap would be meaningless.
                    _lastPos = null;

                    // Races are same-zone by construction, so leaving the zone IS leaving the
                    // course. Ending it here rather than letting the quit-area test catch it means
                    // a race with no quit area still terminates instead of leaving a clock running
                    // across the whole game world.
                    if (_run != null) EndRun(RaceOutcome.LeftArea);

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
        // Before the clears, so the UI is told the run is over rather than watching it vanish.
        if (_run != null) EndRun(RaceOutcome.Abandoned);

        _visited.Clear();
        _sequence.Clear();
        _stepStamp.Clear();
        _armed.Clear();
        _lastPos = null;

        // The inventory map describes the PREVIOUS character's bags; its change events only ever
        // report deltas, so nothing else would ever correct it.
        Plugin.Inventory.Invalidate();
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
            if (!ch.IsWellFormed())              continue;      // half-authored

            // Completed challenges drop out — EXCEPT races, which stay runnable forever so a
            // personal best can be improved. That is the whole point of recording a time: a number
            // you can never beat again is a trophy, not a target. Finishing an already-completed
            // race records the time and does NOT re-fire the completion fanfare (see EvalRace).
            if (_store.IsComplete(ch.Id) && ch.Kind != ChallengeKind.RaceTimer) continue;
            // A chain lives wherever its CURRENT step points, not where it was authored. Gating on
            // the chain's own territory would make a five-zone quest evaluable only in the first
            // zone, and it would stall forever at step two.
            ushort zone = ChallengeCatalog.EffectiveTerritory(ch);
            if (zone != 0 && zone != territory) continue;  // wrong zone

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
        // already passed its position gate actually asks for it. The legacy kinds below use the
        // three locals; the composite kind uses _tick, which generalises the same discipline to
        // every condition type. Both are latched per tick, neither reads anything speculatively.
        _tick.Begin(pos, rotation);

        EquippedSlot[]? equipment = null;
        uint emoteId = 0; bool emoteRead = false;
        uint mountId = 0; bool mountRead = false;

        // Reverse iteration so a completed challenge can be pulled out without disturbing
        // the indices we have not visited yet.
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var ch = _active[i];
            bool done;

            // A chain owns its content through its STEPS, so it is dispatched ahead of Kind —
            // only the current step is ever evaluated, whatever kind the chain itself carries.
            if (ch.IsChain)
            {
                done = EvalChain(ch, pos);
                if (done)
                {
                    _active.RemoveAt(i);
                    MarkComplete(ch);
                }
                continue;
            }

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

                case ChallengeKind.InArea:
                    done = EvalComposite(ch, pos);
                    break;

                case ChallengeKind.RaceTimer:
                    done = EvalRace(ch, pos);
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
    // ── Race ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// One tick of a race challenge: keep the armed list current, and if this is the race being
    /// run, advance its state machine.
    ///
    /// <para><b>Volumes are swept, not point-tested.</b> A race is the one place in this plugin
    /// where the player is deliberately moving as fast as the game allows — mounted, sprinting,
    /// falling — and a finish line is exactly the kind of thin volume that a 5 Hz point sample
    /// steps clean over. Unlike a composite stop there is no state to mispair: crossing the line
    /// at any point during the last 200 ms genuinely is a finish.</para>
    ///
    /// <para>Returns true only on a FIRST completion. A repeat run of an already-completed race
    /// records its time and returns false, so the completion fanfare does not fire again for
    /// something the player has already done.</para>
    /// </summary>
    private bool EvalRace(CustomChallenge ch, Vector3 pos)
    {
        var start = ch.RaceStart;
        var finish = ch.RaceFinish;
        if (start == null || finish == null) return false;

        Vector3 from = _lastPos ?? pos;

        // Arming is independent of running: the prompt needs to know the player is standing at the
        // line whether or not a clock is going. Point test, not swept — "you are here now" is the
        // question, and a sweep would arm a race the player has already run past.
        bool atStartNow = start.Contains(pos);
        if (atStartNow && !IsRaceRunning(ch.Id)) _armed.Add(ch.Id);

        if (_run == null || !string.Equals(_run.Id, ch.Id, StringComparison.OrdinalIgnoreCase))
            return false;

        // (1) Restart on re-entry. Checked before everything else so a runner doubling back to the
        // line gets a clean clock rather than being failed by the old one on the same tick.
        if (!atStartNow && !PassedThrough(start, from, pos))
        {
            _run.LeftStart = true;
        }
        else if (_run.LeftStart)
        {
            _run.StartedAtMs = Environment.TickCount64;
            _run.LeftStart   = false;

            Plugin.Log.Debug($"[Race] {ch.Id} restarted at the line.");
            try { RaceStarted?.Invoke(ch.Id); }
            catch (Exception ex) { Plugin.Log.Error(ex, "Race start handler threw"); }

            return false;
        }

        // (2) Finish. Ahead of the failure checks on purpose: a runner who crosses the line on the
        // very tick their time expires has finished, not failed.
        if (PassedThrough(finish, from, pos))
        {
            double seconds = _run.ElapsedSeconds;
            double? previous = _store.BestRaceTime(ch.Id);
            bool newBest = _store.RecordRaceTime(ch.Id, seconds);
            bool firstTime = !_store.IsComplete(ch.Id);

            EndRun(RaceOutcome.Finished, newBest, previous, firstTime);

            // Only a first finish is a completion. A repeat run has already been recorded above.
            return firstTime;
        }

        // (3) Out of time.
        if (ch.RaceFailSeconds > 0 && _run.ElapsedSeconds > ch.RaceFailSeconds)
        {
            EndRun(RaceOutcome.TimedOut);
            return false;
        }

        // (4) Left the course. Inverted sense — this volume ends the run by being LEFT.
        if (ch.RaceUseQuitArea && ch.RaceQuit != null && !ch.RaceQuit.Contains(pos))
        {
            EndRun(RaceOutcome.LeftArea);
            return false;
        }

        return false;
    }

    // ── Composite (InArea) ───────────────────────────────────────────────────

    /// <summary>
    /// Evaluate a composite challenge: one or more stops, each an area plus the conditions that
    /// must hold inside it.
    ///
    /// <para><b>Why a stop is tested at the point and not swept.</b> The plain visit kinds sweep the
    /// movement since the last tick so a small volume cannot be run past. That is right for "did I
    /// pass through here" and wrong for "was I here WHILE mounted on a Fat Chocobo" — a swept
    /// sample says the player was at that coordinate 40 ms ago, but every condition is read as of
    /// NOW, so a sweep hit would pair an old position with current state and could complete a
    /// challenge for a spot the player was never actually standing in under those conditions.
    ///
    /// A presence-only stop has no such pairing to get wrong, so it keeps the sweep and stays as
    /// forgiving as VisitAreas always was.</para>
    /// </summary>
    private bool EvalComposite(CustomChallenge ch, Vector3 pos)
    {
        var reqs = ch.Requirements;
        if (reqs == null || reqs.Count == 0) return false;

        // An adventure's progress outlives the session unless the author explicitly asked for the
        // old one-sitting constraint. See Configuration.SessionOnly.
        return EvalSet(ch, ch.Id, ch.Mode, reqs, pos, persist: !ch.SessionOnly, announceStops: true);
    }

    /// <summary>
    /// Satisfy one set of stops — the shared engine behind an adventure's objectives and a chain
    /// step's, which are the same shape and differ only in what owns them.
    /// </summary>
    /// <param name="key">
    /// Identity of the set: a challenge id for an adventure, a STEP id for a chain step. Never a
    /// position — reordering steps must not hand a player somebody else's progress.
    /// </param>
    /// <param name="announceStops">
    /// Raise per-stop progress notifications. False for chain steps, where the notification that
    /// matters is "step 2 of 5" and a second one counting stops inside the step would be noise.
    /// </param>
    private bool EvalSet(CustomChallenge ch, string key, AreaMode mode,
                         List<AreaRequirement> reqs, Vector3 pos, bool persist, bool announceStops)
    {
        if (reqs == null || reqs.Count == 0) return false;

        return mode == AreaMode.InOrder
            ? EvalSetOrdered(ch, key, reqs, pos, persist, announceStops)
            : EvalSetAny(ch, key, reqs, pos, persist, announceStops);
    }

    /// <summary>
    /// One step of a quest chain. Only the CURRENT step is ever evaluated — that is what makes a
    /// chain a chain rather than a set — and only finishing the LAST one completes the challenge.
    ///
    /// <para>Chain progress always persists. A chain is explicitly the "take as long as you like"
    /// shape, so a session-scoped one would be a contradiction.</para>
    /// </summary>
    private bool EvalChain(CustomChallenge ch, Vector3 pos)
    {
        int idx = Plugin.Progress.ChainStep(ch.Id);

        // Clamped rather than trusted: a chain edited down to fewer steps must not leave a player
        // pointing past the end of it, and treating that as "finished" would hand out a completion
        // nobody earned.
        if (idx >= ch.ChainSteps.Count) idx = ch.ChainSteps.Count - 1;
        if (idx < 0) return false;

        var step = ch.ChainSteps[idx];
        if (step == null || !step.IsWellFormed()) return false;

        // Keyed by the STEP's id, not by position, so reordering steps while authoring cannot
        // silently hand a player another step's partial progress.
        bool satisfied = EvalSet(ch, step.Id, step.Mode, step.Requirements, pos,
                                 persist: true, announceStops: false);
        if (!satisfied) return false;

        int next = idx + 1;
        bool finished = next >= ch.ChainSteps.Count;

        Plugin.Progress.SetChainStep(ch.Id, next);

        // The step's own stop progress is spent — clearing it keeps the file from accumulating a
        // row per step of every chain the player has ever walked through.
        ClearSetState(step.Id, persist: true);

        _config.StateVersion++;   // the chain's zone may have just changed; force a Rebuild

        Plugin.Log.Information(
            $"[Chain] \"{ch.Title}\" advanced to step {next + 1}/{ch.ChainSteps.Count}.");

        // Same rule as everywhere else: the finishing step is announced by MarkComplete.
        if (!finished && ch.ShowProgress)
            RaiseProgress(ch, next, ch.ChainSteps.Count);

        return finished;
    }

    /// <summary>True when the player is in this stop's area AND all its conditions hold.</summary>
    private bool StopSatisfied(AreaRequirement req, Vector3 pos)
    {
        if (req?.Area == null) return false;

        bool inside = req.Conditions == null || req.Conditions.Count == 0
            ? PassedThrough(req.Area, _lastPos ?? pos, pos)   // presence-only: keep the sweep
            : req.Area.Contains(pos);                          // conditional: must be here NOW

        return inside && ConditionEvaluator.AllHold(req, _tick);
    }

    /// <summary>
    /// <see cref="AreaMode.Single"/> and <see cref="AreaMode.AnyOrder"/>. Single is just the
    /// one-element case of AnyOrder, so it needs no separate path — the set fills to 1 and the
    /// challenge completes.
    /// </summary>
    private bool EvalSetAny(CustomChallenge ch, string key, List<AreaRequirement> reqs,
                            Vector3 pos, bool persist, bool announceStops)
    {
        var set = SetState(key, persist);
        int before = set.Count;

        for (int a = 0; a < reqs.Count; a++)
        {
            if (set.Contains(a)) continue;
            if (StopSatisfied(reqs[a], pos)) set.Add(a);
        }

        bool complete = set.Count >= reqs.Count;
        if (set.Count == before) return complete;

        if (persist) Plugin.Progress.SetStops(key, set);

        // Same rule as every other kind: the finishing step is announced by MarkComplete, so
        // announcing it here too would double the sound and the popup. Single-stop challenges
        // report no partial progress at all — there is none to report.
        if (announceStops && !complete && ChallengeCatalog.HasStepProgress(ch))
            RaiseProgress(ch, set.Count, reqs.Count);

        return complete;
    }

    /// <summary>
    /// The satisfied-stop set for a key, seeded from disk the first time it is touched this
    /// session when the set persists.
    /// </summary>
    private HashSet<int> SetState(string key, bool persist)
    {
        if (_visited.TryGetValue(key, out var set)) return set;

        set = persist ? Plugin.Progress.Stops(key) : new HashSet<int>();
        _visited[key] = set;
        return set;
    }

    /// <summary>Forget a set entirely, on disk too when it persists.</summary>
    private void ClearSetState(string key, bool persist)
    {
        _visited.Remove(key);
        _sequence.Remove(key);
        _stepStamp.Remove(key);
        if (persist) Plugin.Progress.Clear(key);
    }

    /// <summary>
    /// <see cref="AreaMode.InOrder"/>. Only the NEXT stop is tested, which is what "in order"
    /// means, and a stop may carry a <see cref="AreaRequirement.WithinSeconds"/> budget measured
    /// from the moment the previous one was satisfied — the "within X seconds of Y" relation.
    /// </summary>
    private bool EvalSetOrdered(CustomChallenge ch, string key, List<AreaRequirement> reqs,
                                Vector3 pos, bool persist, bool announceStops)
    {
        // Ordered progress is a PREFIX of satisfied indices, so the persisted set's count is the
        // index. One storage shape serves both modes rather than a second map that could drift
        // out of agreement with this one.
        int idx = _sequence.TryGetValue(key, out var v) ? v : SetState(key, persist).Count;
        if (idx >= reqs.Count) return true;

        var next = reqs[idx];
        if (!StopSatisfied(next, pos)) { _sequence[key] = idx; return false; }

        // Timed step: the clock runs from when the PREVIOUS stop landed. Blowing the budget resets
        // the whole sequence rather than merely failing this step — a partially-run ordered
        // challenge that stayed half-complete would let the player stroll the remainder untimed.
        if (idx > 0 && next.WithinSeconds > 0)
        {
            long since = Environment.TickCount64 - (_stepStamp.TryGetValue(key, out var t) ? t : 0);
            if (since > next.WithinSeconds * 1000L)
            {
                Plugin.Log.Debug(
                    $"[Tracker] \"{ch.Title}\" step {idx + 1} missed its {next.WithinSeconds}s window "
                  + $"({since / 1000f:0.#}s) — sequence reset.");

                _sequence[key] = 0;
                _stepStamp.Remove(key);
                _visited.Remove(key);
                if (persist) Plugin.Progress.SetStops(key, new HashSet<int>());
                return false;
            }
        }

        idx++;
        _sequence[key]  = idx;
        _stepStamp[key] = Environment.TickCount64;

        if (persist)
        {
            var prefix = new HashSet<int>();
            for (int i = 0; i < idx; i++) prefix.Add(i);
            _visited[key] = prefix;
            Plugin.Progress.SetStops(key, prefix);
        }

        bool complete = idx >= reqs.Count;

        if (announceStops && !complete && ChallengeCatalog.HasStepProgress(ch))
            RaiseProgress(ch, idx, reqs.Count);

        return complete;
    }

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
        _stepStamp.Remove(ch.Id);

        // Persisted partial progress is spent once the challenge is done. Left behind, a later
        // Reset would clear the completion but restore a half-finished state for it.
        Plugin.Progress.Clear(ch.Id);
        if (ch.IsChain)
            foreach (var s in ch.ChainSteps) ClearSetState(s.Id, persist: true);

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
