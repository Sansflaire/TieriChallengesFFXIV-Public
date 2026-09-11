#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. <b>Test 4 — Wild Trail.</b> Test 3's ordered clue trail, except the stops
/// and the clues are GENERATED for the map the player is standing on.
///
/// <para><b>This is not the trail generator that was removed, and the difference is the landmark
/// list.</b> The old one produced "NORTH-EAST, far away" — a bearing from wherever the player
/// happened to stand, which is a search order rather than a clue. Every clue here is anchored to
/// something the game itself draws on the map, so it names a place the player can go to and then
/// search around. See <see cref="DigLandmarks"/>.</para>
///
/// <para><b>Placement obeys Test 2's ground rules.</b> A spot must sit on real geometry within
/// <see cref="DigTuning.MaxRise"/> of the player's height — which is what keeps it off rooftops and
/// clifftops in a no-flying zone — and must pass the step test, so it never straddles a wall, a kerb
/// or a ledge. Both are the rules the site test already uses; neither is new here.</para>
///
/// <para><b>Reachability is a heuristic, not a proof.</b> Nothing consulted here knows whether the
/// player can actually WALK to a point: a raycast finds ground, and ground inside a sealed building
/// is still ground. The height rule and the step rule together reject the common cases — roofs,
/// ledges, the far side of a wall you would have to fly over — and a spot inside a locked house
/// would still get through. A navmesh query is the only real answer and this does not have one.</para>
/// </summary>
internal sealed unsafe class DigRoamService : IDigTest
{
    private enum Phase { Off, Running, Done }

    private const long DoneHoldMs = 15_000;

    /// <summary>
    /// One buried spot and the clue that leads to it. Named <c>Buried</c> rather than <c>Stop</c>
    /// because <see cref="IDigTest.Stop"/> already owns that name on this type.
    /// </summary>
    private sealed class Buried
    {
        public Vector3 Position;
        public string  Clue = string.Empty;
    }

    private readonly List<Buried> _stops = new();
    private readonly Random       _rng   = new();

    private Phase _phase;
    private long  _startedAtMs;
    private long  _endedAtMs;
    private int   _index;
    private int   _digs;
    private float _distance = float.MaxValue;
    private uint  _territory;

    public string Name => "Wild Trail";

    public bool IsActive   => _phase != Phase.Off;
    public bool IsFinished => _phase == Phase.Done;

    public int   StopTotal     => _stops.Count;
    public int   StopIndex     => _index;
    public int   Digs          => _digs;
    public float DebugDistance => _distance;

    private Buried? Current =>
        _phase == Phase.Running && _index >= 0 && _index < _stops.Count ? _stops[_index] : null;

    public DigBand Band => _phase switch
    {
        Phase.Done    => DigBand.Green,
        Phase.Running => _distance <= DigTuning.RoamDig   ? DigBand.Dig
                       : _distance <= DigTuning.RoamRadar ? DigBand.Yellow
                       : DigBand.Cold,
        _ => DigBand.Cold,
    };

    /// <summary>0 at the edge of radar range, 1 on the spot. Null whenever there is no radar.</summary>
    public float? RadarCloseness
    {
        get
        {
            if (_phase != Phase.Running) return null;

            float outer = MathF.Max(DigTuning.RoamRadar, DigTuning.RoamDig + 1f);
            if (_distance > outer) return null;

            float inner = DigTuning.RoamDig;
            if (_distance <= inner) return 1f;

            return Math.Clamp(1f - (_distance - inner) / (outer - inner), 0f, 1f);
        }
    }

    public double ElapsedSeconds => _phase switch
    {
        Phase.Running => (Environment.TickCount64 - _startedAtMs) / 1000.0,
        Phase.Done    => (_endedAtMs - _startedAtMs) / 1000.0,
        _             => 0.0,
    };

    public string Headline => _phase switch
    {
        Phase.Done    => "TRAIL'S END!",
        Phase.Running => $"CLUE  {_index + 1} / {_stops.Count}",
        _             => "WILD TRAIL",
    };

    /// <summary>The clue, or nothing. Same rule as Test 3 — this slot never holds anything else.</summary>
    public string Subtitle => Current?.Clue ?? string.Empty;

    // ── the player's actions ─────────────────────────────────────────────────

    public string Start()
    {
        // NO CanPerform GATE. Starting buries spots and writes clues; it does not touch game code
        // or play an animation, so refusing it because the player is mounted was refusing something
        // that was never at risk. The check belongs on the DIG, which is the part that performs.
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return "no character loaded.";

        // Refuse to start on a half-built mesh rather than quietly placing what fits.
        //
        // Placement itself is happy to query a partial mesh — the parts already built answer
        // correctly. A RUN is not, because the spots it could place would all sit in whichever
        // corner of the zone happened to be meshed first, and the player would never know the trail
        // had been biased. Waiting is cheap; a silently lopsided trail is not.
        if (DigTuning.RoamRequireNavmesh && DigNavmesh.Available && !DigNavmesh.Ready)
            return DigNavmesh.StatusLine() + " — wait for it to finish, then start again.";

        // Re-read every time: the map's landmarks are what the clues are made of, and a cached set
        // from the previous zone would write clues about places that are not here.
        DigLandmarks.Invalidate();

        var landmarks = DigLandmarks.ForCurrentMap();

        _stops.Clear();
        _territory   = Plugin.ClientState.TerritoryType;
        _navRefusals = 0;

        int want = Math.Clamp(DigTuning.RoamStops, 1, 20);

        var placed = new List<Vector3>();

        for (int i = 0; i < want; i++)
        {
            if (!TryPlace(player.Position, placed, out var spot)) break;

            placed.Add(spot);
            _stops.Add(new Buried { Position = spot });
        }

        if (_stops.Count == 0)
            return _navRefusals > 0
                ? "refused to bury anything: vnavmesh is not answering, so nothing here can be "
                + "confirmed walkable. Install/enable vnavmesh, or turn off \"Require navmesh\" in "
                + "the lab to fall back to the old guesswork."
                : "found nowhere to bury anything here — try somewhere more open.";

        // Clues are written AFTER every spot is placed, so a clue may refer to another stop's
        // surroundings without the writing order deciding what it is allowed to know.
        foreach (var s in _stops) s.Clue = Clue(s.Position, landmarks);

        _index       = 0;
        _digs        = 0;
        _distance    = float.MaxValue;
        _startedAtMs = Environment.TickCount64;
        _phase       = Phase.Running;

        string shortfall = _stops.Count < want
            ? $" (wanted {want}, the ground only allowed {_stops.Count})"
            : string.Empty;

        return $"{_stops.Count} spot(s) buried{shortfall}. First clue: {_stops[0].Clue}";
    }

    /// <summary>
    /// A buried spot on ground that passes both of Test 2's rules.
    ///
    /// <para>Rejection sampling rather than anything cleverer, because "valid ground" is only
    /// answerable by asking the geometry — there is no list of standable points to draw from.</para>
    /// </summary>
    /// <summary>Candidates thrown away purely because the navmesh could not be consulted.</summary>
    private int _navRefusals;

    private bool TryPlace(Vector3 from, List<Vector3> taken, out Vector3 spot)
    {
        spot = default;

        float min = MathF.Max(0f, DigTuning.RoamMinRange);
        float cap = MathF.Max(0f, DigTuning.RoamMaxRange);

        // THE WHOLE MAP IS ELIGIBLE. Spots are drawn from the map's own world rectangle rather than
        // from a ring around the player, so anywhere the drawn map covers can hold one. The old ring
        // made "how far did you walk before starting" decide what the trail could contain.
        bool haveBounds = DigLandmarks.TryWorldBounds(out var lo, out var hi);

        for (int attempt = 0; attempt < PlacementAttempts; attempt++)
        {
            if (haveBounds)
            {
                float x = lo.X + (float)_rng.NextDouble() * (hi.X - lo.X);
                float z = lo.Y + (float)_rng.NextDouble() * (hi.Y - lo.Y);

                // No player-relative rise gate: across a whole zone it would reject every hill and
                // basement for being far from where the player happens to stand. The roof question
                // is asked below instead, by something local enough to stay true anywhere.
                if (!DigGround.TryGroundAt(from, x, z, 0f, out spot)) continue;
            }
            else if (!DigGround.TryPointNear(from, MathF.Max(5f, min),
                                             MathF.Max(min + 5f, cap > 0f ? cap : 220f),
                                             _rng, out spot, attempts: 12))
            {
                continue;
            }

            float fromPlayer = DigGround.Flat(from, spot);

            // Not under the player's feet, and inside the optional cap. Cap 0 means no cap, which
            // is the default and the whole point.
            if (fromPlayer < min) continue;
            if (cap > 0f && fromPlayer > cap) continue;

            // THE REACHABILITY GATE, and the only one of these that actually answers the question.
            //
            // A raycast finds the first solid surface under a position, and a housing ward is
            // surrounded by terrain that is perfectly solid and completely unreachable — the sand
            // outside the walls, the ground beneath the skybox. Spots were landing out there and
            // being described with a straight face, because every other test here narrows the
            // problem without knowing what "reachable" means. A navmesh IS the set of places a
            // character can walk, so it does know.
            bool? walkable = DigNavmesh.IsWalkable(spot, DigTuning.RoamNavTolerance);

            if (walkable == false) continue;

            // Null means vnavmesh could not answer at all — genuinely not installed.
            //
            // THE DEFAULT IS TO REFUSE, and that is a deliberate reversal. It used to fall back to
            // the raycast heuristics, which meant the single most important property of a spot —
            // that the player can reach it — was quietly downgraded to a guess whenever the one
            // component that knows the answer was missing. Placing nothing is a visible failure
            // somebody fixes; placing something unreachable is an invisible one they waste ten
            // minutes walking into.
            if (walkable == null)
            {
                if (DigTuning.RoamRequireNavmesh) { _navRefusals++; continue; }
                if (DigGround.IsElevated(spot, DigTuning.RoamMaxRise)) continue;
            }

            // Far enough from the others that no single dig can turn up two, and so the trail is a
            // route rather than a huddle.
            bool crowded = false;
            foreach (var t in taken)
            {
                if (DigGround.Flat(t, spot) >= DigTuning.RoamSpacing) continue;
                crowded = true;
                break;
            }

            if (crowded) continue;

            // The wall-versus-hill test. Without it a spot can sit astride a kerb, where half its
            // dig radius is somewhere the player cannot stand.
            if (DigGround.HasStep(spot, DigTuning.RoamDig, DigTuning.SitePieceMaxStep)) continue;

            return true;
        }

        spot = default;
        return false;
    }

    private const int PlacementAttempts = 120;

    /// <summary>
    /// Places <paramref name="count"/> spots WITHOUT starting a run, for the map debug view.
    ///
    /// <para><b>It goes through the same <see cref="TryPlace"/> a real run uses, deliberately.</b> A
    /// separate sampler written for the debug view would be testing itself rather than the thing
    /// that actually buries spots — and the whole point of plotting them is to find out whether the
    /// real placement can put one somewhere impossible.</para>
    ///
    /// <para>Spacing is honoured between samples, so the picture is a plausible dense run rather
    /// than a cloud of points that would never coexist.</para>
    /// </summary>
    public List<Vector3> SampleCandidates(int count)
    {
        var found = new List<Vector3>();

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return found;

        for (int i = 0; i < count; i++)
        {
            if (!TryPlace(player.Position, found, out var spot)) break;
            found.Add(spot);
        }

        return found;
    }

    public string Dig()
    {
        string animation = Plugin.Props.Dig();

        if (_phase != Phase.Running) return animation;

        _digs++;

        if (_distance > DigTuning.RoamDig) return animation + " Nothing but dirt here.";

        _pendingAdvance = true;
        return animation + " You strike something…";
    }

    private bool _pendingAdvance;

    public void DigFinished(bool completed)
    {
        bool advance = _pendingAdvance;
        _pendingAdvance = false;

        if (!advance || _phase != Phase.Running) return;

        if (!completed)
        {
            Plugin.ChatGui.Print("[Challenges] You stopped digging too soon — the clue stays buried.");
            return;
        }

        _index++;

        if (_index < _stops.Count)
        {
            try { Plugin.Sound.Play(SoundService.Cue.ObjectiveProgress); }
            catch (Exception ex) { Diag.Error($"[Roam] clue cue failed: {ex.Message}"); }

            Plugin.ChatGui.Print($"[Challenges] A clue! {_stops[_index].Clue}");
            return;
        }

        _endedAtMs = Environment.TickCount64;
        _phase     = Phase.Done;

        try { Plugin.Sound.Play(SoundService.Cue.ChallengeComplete); }
        catch (Exception ex) { Diag.Error($"[Roam] end cue failed: {ex.Message}"); }

        string time = CompletionStore.FormatRaceTime(ElapsedSeconds);
        Plugin.ChatGui.Print($"[Challenges] That's the end of the trail! {time}, {_digs} dig(s).");
    }

    public string Stop()
    {
        if (_phase == Phase.Off) return "no wild trail is running.";
        _phase = Phase.Off;
        _stops.Clear();
        return "wild trail abandoned.";
    }

    public string Recall() =>
        _phase != Phase.Running ? "no wild trail is running." : Current?.Clue ?? "no clue.";

    /// <summary>
    /// Gives the current spot away: the truth about where it is, and a flag on the map.
    ///
    /// <para><b>This is a debugging tool and it exists because a generated clue can be wrong in two
    /// completely different ways that look identical from inside the game.</b> The clue may be
    /// unhelpful — an anchor that is an area label rather than a point, say — or the whole
    /// marker-to-world conversion may be off, in which case the clue is describing a position the
    /// spot is not at. From the player's side both are just "I cannot find it".</para>
    ///
    /// <para>The readout separates them. It prints where the spot really is, where the clue's anchor
    /// really is, and the true bearing and distance between them. If the bearing matches the clue's
    /// wording, the conversion is sound and the clue is merely hard; if it does not, the conversion
    /// is the bug and no amount of clue tuning will help.</para>
    /// </summary>
    public string Reveal()
    {
        if (_phase != Phase.Running) return "no wild trail is running.";

        var current = Current;
        if (current == null) return "no spot.";

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return "no character loaded.";

        var spot = current.Position;

        string line = $"spot {_index + 1}/{_stops.Count}: "
                    + $"{DigGround.Flat(player.Position, spot):0.0}y {DigGround.Compass(player.Position, spot)} of you";

        if (DigLandmarks.TryMapCoords(spot, out float mx, out float my))
            line += $"   map ({mx:0.0}, {my:0.0})";

        line += $"   world ({spot.X:0.0}, {spot.Y:0.0}, {spot.Z:0.0})";

        // The anchor the clue was written from, and the TRUE relationship to the spot. A clue that
        // says WEST while this says EAST is a conversion bug, not a hard clue.
        if (DigLandmarks.Nearest(spot) is { } near)
            line += $"\n[Challenges] clue anchor \"{near.Name}\" is at world "
                  + $"({near.World.X:0.0}, {near.World.Z:0.0}), "
                  + $"{DigGround.Flat(near.World, spot):0.0}y away — spot is truly "
                  + $"{DigGround.Compass(near.World, spot)} of it";

        try
        {
            unsafe
            {
                var agent = AgentMap.Instance();
                if (agent != null)
                {
                    // The Vector3 overload takes WORLD coordinates and converts internally — see
                    // CLAUDE.md's map-pin rules. Handing it map coordinates would put the flag in
                    // the wrong place and make this diagnostic lie.
                    agent->SetFlagMapMarker((uint)_territory, DigLandmarks.CurrentMapId(), spot);
                    agent->OpenMap(DigLandmarks.CurrentMapId(), (uint)_territory, null, MapType.FlagMarker);
                }
            }
        }
        catch (Exception ex) { Diag.Error($"[Roam] reveal flag failed: {ex.Message}"); }

        return line;
    }

    public void DrawWorld() { /* drawing the stops would replace the clue with an answer */ }

    public void Tick()
    {
        if (_phase == Phase.Off) return;

        try
        {
            if (_phase == Phase.Done)
            {
                if (Environment.TickCount64 - _endedAtMs >= DoneHoldMs) Stop();
                return;
            }

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null || !Plugin.ClientState.IsLoggedIn) { Stop(); return; }

            // Unlike the authored trail, this one DOES abandon on a zone change: every spot was
            // placed against this map's geometry and every clue names this map's landmarks, so
            // neither means anything one zone over.
            if (Plugin.ClientState.TerritoryType != _territory)
            {
                Plugin.ChatGui.Print("[Challenges] Left the zone — the wild trail is abandoned.");
                Stop();
                return;
            }

            var current = Current;
            if (current == null) { Stop(); return; }

            _distance = DigGround.Flat(player.Position, current.Position);
        }
        catch (Exception ex)
        {
            Diag.Error($"[Roam] tick failed: {ex.Message}");
            _phase = Phase.Off;
        }
    }

    // ── clue writing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Turns a buried position into something a person can act on, at the difficulty asked for.
    ///
    /// <para><b>Difficulty is how much is WITHHELD, not how vague the words are.</b> An easy clue
    /// names one landmark, a direction from it and a sense of distance — go there, walk that way,
    /// sweep. A hard one gives the same position through constraints the player has to intersect
    /// themselves: two landmarks and no bearing, or a quadrant and nothing else. Neither is ever a
    /// coordinate, because a coordinate is not a clue, it is the answer.</para>
    ///
    /// <para><b>Every anchor is a place the game draws on the map.</b> That is what makes a
    /// generated clue usable at all — the player can open the map, find the word, and go. A bearing
    /// from where they happened to be standing, which is what the old generator produced, cannot be
    /// looked up anywhere.</para>
    /// </summary>
    private static string Clue(Vector3 spot, IReadOnlyList<DigLandmarks.Landmark> landmarks)
    {
        float hard = Math.Clamp(DigTuning.RoamDifficulty, 0f, 1f);

        var    nearest = DigLandmarks.Nearest(spot);
        string quad    = DigLandmarks.Quadrant(spot);

        // No named landmarks on this map at all — some zones genuinely have none. The quadrant is
        // then the only honest thing that can be said, and saying it plainly beats inventing an
        // anchor that does not exist.
        if (nearest is not { } near)
            return landmarks.Count == 0
                ? $"Somewhere in {quad}. (This map has no named landmarks to go by.)"
                : $"Somewhere in {quad}.";

        float dist = DigGround.Flat(spot, near.World);

        // EASY — a place, a direction and a rough distance. Three of the four things needed.
        if (hard < 0.34f)
            return $"{DigGround.Compass(near.World, spot)} of {near.Name}, {DigGround.Vagueness(dist)}.";

        // MEDIUM — the place and the direction, but no sense of how far. The player knows which way
        // to set off and has to decide for themselves when they have gone too far.
        if (hard < 0.67f)
            return $"{DigGround.Compass(near.World, spot)} of {near.Name}, in {quad}.";

        // HARD — two anchors and no bearing at all. The position is the intersection of "near this"
        // and "near that", which the player has to work out by looking at the map rather than by
        // walking in a straight line. Falls back to a quadrant-only clue on a map with one
        // landmark, which is harder still and is the honest thing to say when there is nothing to
        // triangulate against.
        var second = DigLandmarks.Nearest(spot, near);

        if (second is not { } far)
            return $"Buried in {quad}, nearer {near.Name} than anywhere else named.";

        float farDist = DigGround.Flat(spot, far.World);

        return farDist > dist
            ? $"Between {near.Name} and {far.Name}, closer to the former. Look in {quad}."
            : $"Between {near.Name} and {far.Name}, closer to the latter. Look in {quad}.";
    }
}
#endif
