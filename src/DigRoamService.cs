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
        public Vector3      Position;
        public string       Clue     = string.Empty;
        public ClueCategory Category = ClueCategory.None;
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

    /// <summary>
    /// Monotonic counters the HUD edge-detects to fire the TRAIL START / TRAIL END banners.
    ///
    /// <para><b>Test 4 had neither, so it never showed a banner at all.</b> The overlay watched
    /// <c>DigTrailService</c> by concrete type — correct when Test 3 was the only trail, and quietly
    /// wrong the moment a second trail existed. The banners are a property of "a trail started" and
    /// not of "Test 3 started", so both now expose the same counters and the overlay watches their
    /// sum.</para>
    ///
    /// <para>Counters rather than state levels, for the reason recorded on the same pair in
    /// <c>DigTrailService</c>: a finished trail lingers before it stops, so a poller watching for a
    /// change of level misses a restart inside that window.</para>
    /// </summary>
    public int StartCount  { get; private set; }
    public int FinishCount { get; private set; }

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

    /// <summary>
    /// Raised when a trail is FINISHED — seconds taken and digs spent. Never raised for an
    /// abandoned one, which is what keeps <see cref="ActivityRuns"/> free of times that are not
    /// results.
    /// </summary>
    public event Action<double, int>? Finished;

    /// <summary>Raised when a RUNNING trail is stopped before its last clue.</summary>
    public event Action? Abandoned;

    public string Start() => Start(null, null);

    /// <summary>
    /// Starts a trail, optionally overriding the lab's own clue count and difficulty.
    ///
    /// <para><b>Overrides are passed, never written into <see cref="DigTuning"/>.</b> The Activity
    /// mode chooses its own difficulty and clue count from its own controls; routing them through
    /// the global tuning to be read straight back would make the lab's sliders jump to whatever the
    /// last activity used, and would make an activity in progress change shape the moment somebody
    /// opened the lab. Null means "use the lab's value", which is exactly what the lab wants.</para>
    /// </summary>
    public string Start(int? stops, float? difficulty)
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
        if (RefuseWithoutNavmesh() is { } refusal) return refusal;

        // Re-read every time: the map's landmarks are what the clues are made of, and a cached set
        // from the previous zone would write clues about places that are not here.
        DigLandmarks.Invalidate();
        DigClueSources.Invalidate();

        var landmarks = DigLandmarks.ForCurrentMap();

        _stops.Clear();
        _territory   = Plugin.ClientState.TerritoryType;
        _navRefusals = 0;

        int want = Math.Clamp(stops ?? DigTuning.RoamStops, 1, 20);

        var placed = new List<Vector3>();

        for (int i = 0; i < want; i++)
        {
            if (!TryPlace(player.Position, placed, out var spot)) break;

            placed.Add(spot);
            _stops.Add(new Buried { Position = spot });
        }

        if (_stops.Count == 0)
            return _navRefusals > 0
                ? $"refused to bury anything — every one of {PlacementAttempts} candidates was "
                + "rejected as unreachable. That is the gate working, not failing: this map's "
                + "reachable area may be small, or vnavmesh stopped answering mid-run."
                : "found nowhere to bury anything here — try somewhere more open.";

        // Clues are written AFTER every spot is placed, so a clue may refer to another stop's
        // surroundings without the writing order deciding what it is allowed to know.
        //
        // ONE writer for the whole trail, because the anti-repeat state IS the writer. A fresh one
        // per clue would start every draw from equal weights and the trail could say the same kind
        // of thing five times, which is exactly what this replaced.
        var writer = new DigClueWriter(_rng);

        float hard = Math.Clamp(difficulty ?? DigTuning.RoamDifficulty, 0f, 1f);

        foreach (var s in _stops)
        {
            s.Clue     = writer.Write(s.Position, hard);
            s.Category = writer.LastCategory;
        }

        _index       = 0;
        _digs        = 0;
        _distance    = float.MaxValue;
        _startedAtMs = Environment.TickCount64;
        _phase       = Phase.Running;

        // Bumped only once the start has actually succeeded — every refusal above returns before
        // here, so the banner cannot announce a trail that never began.
        StartCount++;

        string shortfall = _stops.Count < want
            ? $" (wanted {want}, the ground only allowed {_stops.Count})"
            : string.Empty;

        return $"{_stops.Count} spot(s) buried{shortfall}. First clue: {_stops[0].Clue}";
    }

    /// <summary>Candidates thrown away purely because the navmesh could not be consulted.</summary>
    private int _navRefusals;

    /// <summary>
    /// Why candidates were rejected, per gate. <b>Four gates now stand between a random point and a
    /// buried spot, and "0 placed" says nothing about which one did it.</b> Guessing between them is
    /// what this session has repeatedly got wrong, so they are counted instead.
    /// </summary>
    private int _rejGround, _rejRange, _rejBox, _rejAnchor, _rejNav, _rejCrowd, _rejStep, _rejRoof,
                _rejWall, _rejNull;

    /// <summary>
    /// Where each rejected candidate was and which gate rejected it — recorded only while the debug
    /// sampler is running.
    ///
    /// <para><b>A count says how many; this says WHERE.</b> The tally answers "which gate is doing
    /// the work" and cannot answer "why is that whole plaza empty" — those are different questions.
    /// A gate firing 2000 times spread evenly and one firing 2000 times over a single region produce
    /// the same number and mean completely different things. Plotting the rejections turns the
    /// second question into a picture.</para>
    ///
    /// <para>Positions only exist once a candidate has found ground, so "no ground" contributes
    /// nothing here — correct, since that rejection is the void outside the zone and has no location
    /// worth drawing.</para>
    /// </summary>
    public readonly List<(Vector3 Pos, string Reason)> Rejected = new();

    /// <summary>Off for real runs: a trail has no use for it and it is pure cost.</summary>
    private bool _recordRejects;

    /// <summary>Bounded, so a 10,000-sample run cannot grow this without limit.</summary>
    private const int MaxRecordedRejects = 6000;

    private void Reject(string reason, Vector3 at, ref int counter)
    {
        counter++;

        if (!_recordRejects || Rejected.Count >= MaxRecordedRejects) return;
        Rejected.Add((at, reason));
    }

    private void ResetRejections() =>
        _rejGround = _rejRange = _rejBox = _rejAnchor = _rejNav = _rejCrowd = _rejStep =
        _rejRoof = _rejWall = _rejNull = 0;

    /// <summary>A human-readable tally of the last placement run's rejections.</summary>
    public string RejectionReport =>
        $"no ground {_rejGround}   out of range {_rejRange}   outside box {_rejBox}   "
      + $"in a null zone {_rejNull}   "
      + $"no marker within {DigTuning.RoamMaxAnchorDistance:0}y {_rejAnchor}   "
      + $"not on reachable navmesh {_rejNav}   ON A ROOF {_rejRoof}   WALLED OFF {_rejWall}   "
      + $"too close to another {_rejCrowd}   step/ledge {_rejStep}";

    /// <param name="spacing">
    /// Whether the minimum gap between spots applies. <b>True for a real trail and false for the
    /// debug sampler</b>, which is the whole difference between the two callers: a trail's stops
    /// must be far enough apart that one dig cannot turn up two clues, while a sample is an
    /// independent answer to "could a spot land here" and has no relationship to the other samples.
    /// Applying the trail rule to the sampler is why it plateaued around 20 of 200 — every
    /// additional sample made the next one harder to place, in a ward barely wider than a few
    /// spacings.
    /// </param>
    private bool TryPlace(Vector3 from, List<Vector3> taken, out Vector3 spot, float nearOnly = 0f,
                          bool spacing = true)
    {
        spot = default;

        float min = MathF.Max(0f, DigTuning.RoamMinRange);
        float cap = MathF.Max(0f, DigTuning.RoamMaxRange);

        // A near-only placement overrides both range knobs — it is used by the single-category test
        // spawn, whose whole point is that the spot is within walking distance so the CLUE can be
        // judged rather than the walk.
        if (nearOnly > 0f) { min = MathF.Min(10f, nearOnly * 0.25f); cap = nearOnly; }

        // THE WHOLE MAP IS ELIGIBLE. Spots are drawn from the map's own world rectangle rather than
        // from a ring around the player, so anywhere the drawn map covers can hold one. The old ring
        // made "how far did you walk before starting" decide what the trail could contain.
        // SAMPLE FROM THE WALKABLE BOX, NOT THE MAP RECTANGLE.
        //
        // This is why only ~19 of 200 were ever placed. The map rectangle is the whole theoretical
        // coordinate square and a housing ward occupies perhaps a tenth of it; the rest is void with
        // no collision at all. So roughly 95% of throws died on "no ground" before any other gate
        // saw them — 1863 of them in one run — and the attempt budget was exhausted long before the
        // sampler had found enough real candidates.
        //
        // The box already describes where the ward is. Throwing darts at it instead of at the
        // surrounding emptiness costs nothing and raises the hit rate by an order of magnitude.
        // Nothing about which spots are ACCEPTABLE changes — the box gate below already rejected
        // everything outside it, so this only stops wasting throws on ground that was never
        // eligible.
        Vector2 lo = default, hi = default;
        bool haveBounds = nearOnly <= 0f
                       && (DigLandmarks.WalkableBox(out lo, out hi)
                           || DigLandmarks.TryWorldBounds(out lo, out hi));

        for (int attempt = 0; attempt < PlacementAttempts; attempt++)
        {
            if (haveBounds)
            {
                float x = lo.X + (float)_rng.NextDouble() * (hi.X - lo.X);
                float z = lo.Y + (float)_rng.NextDouble() * (hi.Y - lo.Y);

                // No player-relative rise gate: across a whole zone it would reject every hill and
                // basement for being far from where the player happens to stand. The roof question
                // is asked below instead, by something local enough to stay true anywhere.
                if (!DigGround.TryGroundAt(from, x, z, 0f, out spot)) { _rejGround++; continue; }
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
            if (fromPlayer < min)               { _rejRange++; continue; }
            if (cap > 0f && fromPlayer > cap)   { _rejRange++; continue; }

            // THE REACHABILITY GATE, and the only one of these that actually answers the question.
            //
            // A raycast finds the first solid surface under a position, and a housing ward is
            // surrounded by terrain that is perfectly solid and completely unreachable — the sand
            // outside the walls, the ground beneath the skybox. Spots were landing out there and
            // being described with a straight face, because every other test here narrows the
            // problem without knowing what "reachable" means. A navmesh IS the set of places a
            // character can walk, so it does know.
            // INSIDE THE BOX THE CLUES ARE WRITTEN AGAINST, first and cheaply.
            //
            // The debug view showed candidates landing far outside the ward while passing the
            // navmesh gate — so the navmesh gate is not sufficient on its own, whatever its filter
            // means. This is the blunt fix and it is the honest one: a spot outside the area the
            // map puts NAMES in is a spot no clue could describe anyway, because every anchor a
            // clue could reach for is inside that area. Rejecting it costs nothing and it cannot
            // be wrong in the direction that matters.
            if (DigLandmarks.WalkableBox(out var boxLo, out var boxHi) &&
                (spot.X < boxLo.X || spot.X > boxHi.X || spot.Z < boxLo.Y || spot.Z > boxHi.Y))
            { Reject("outside box", spot, ref _rejBox); continue; }

            // HAND-DRAWN EXCLUSIONS. Checked right after the box because together they are the
            // authored answer to "where may a spot go", and both are cheap.
            //
            // This is the gate that does not need to be clever. Every automatic attempt to work out
            // which ground is off-limits has failed in a different way; a rectangle somebody drew
            // over the lake cannot be wrong about the lake.
            // THE LIVE TERRITORY, NOT THE FIELD. _territory is only assigned when a RUN starts, and
            // the debug sampler never starts a run — so it read 0, matched no zones, and the gate
            // silently did nothing while rectangles sat plainly on the map. Reading the zone the
            // player is actually in cannot be stale and cannot be unset.
            if (DigTuning.InNullZone(Plugin.ClientState.TerritoryType, spot.X, spot.Z))
            { Reject("null zone", spot, ref _rejNull); continue; }

            // NEAR SOMETHING THE MAP NAMES — the containment that follows the zone's SHAPE.
            //
            // A box cannot. Empyreum's ward is not a rectangle, so a box drawn round it has corners
            // well outside the walls and spots kept landing in them. The markers do follow the
            // outline: plot numbers, subdivision labels and shops blanket the walkable area and
            // stop dead at its edge, so "is a marker near" traces the ward for free.
            //
            // This is the gate doing the work the navmesh filter was wrongly trusted to do. It is a
            // proxy rather than a proof — an open field legitimately has no marker in the middle of
            // it, which is why it is tunable and why 0 disables it.
            if (DigTuning.RoamMaxAnchorDistance > 0f)
            {
                var near = DigLandmarks.Nearest(spot);

                if (near is not { } anchor ||
                    DigGround.Flat(anchor.World, spot) > DigTuning.RoamMaxAnchorDistance)
                { Reject("no marker near", spot, ref _rejAnchor); continue; }
            }

            // Asks for REACHABLE mesh, which is a different question from "is there mesh here" and
            // is the one that was being got wrong. Unreachable polygons are flagged by a flood fill
            // at mesh build time, so an island cut off from the walkable body of the zone — the sand
            // outside the walls — is rejected even though it is unambiguously real ground.
            bool? walkable = DigNavmesh.TrySnapToReachable(spot, DigTuning.RoamNavTolerance,
                                                           out var onMesh);

            // Null means vnavmesh could not answer at all. It is a hard requirement of this test and
            // Start refused before we got here, so reaching this is a mid-run disappearance — treat
            // it exactly like a no.
            if (walkable != true)
            { _navRefusals++; Reject("off navmesh", spot, ref _rejNav); continue; }

            // SNAP TO THE MESH, then refine the height DOWNWARD FROM THE MESH POINT.
            //
            // THIS LINE PUT SPOTS ON ROOFTOPS. It used to re-ground with TryGroundAt, which starts
            // its ray 120 yalms up and takes the first surface it meets — over a house, the roof. So
            // the navmesh would correctly return a point on the path outside a building, and this
            // would then hoist it onto the building. Every later gate agreed it was fine, because in
            // XZ it IS fine; only the height was wrong, and nothing downstream looks at height.
            //
            // Refining from just above the mesh point cannot relocate the spot onto something else:
            // it only corrects the small offset between the navmesh surface, which is a
            // simplification, and the visual floor.
            spot = DigGround.TryGroundBelow(onMesh, 2f, 6f, out var settled) ? settled : onMesh;

            // THE ROOF TEST, and it had become dead code. It used to live inside an
            // `if (walkable == null)` branch — the vnavmesh-not-installed path — so the moment the
            // navmesh started answering, the one check specifically designed to reject rooftops
            // stopped running at all. It belongs here, unconditionally, on the final position.
            if (DigGround.IsElevated(spot, DigTuning.RoamMaxRise))
            { Reject("roof", spot, ref _rejRoof); continue; }

            // THE "WALLED OFF" GATE IS GONE, AND IT WAS INCOHERENT RATHER THAN MISTUNED.
            //
            // It walked a line from the candidate to its NEAREST MARKER and rejected the candidate
            // if anything blocked it. But which marker is nearest is an accident of where labels
            // happen to sit: an open plaza's nearest marker is routinely on the far side of a
            // building, so a spot standing in the middle of clear paving failed a test about a
            // sightline it had no relationship to.
            //
            // It never measured whether the SPOT was enclosed — only whether one arbitrary anchor
            // was visible from it. Sansflaire's observation that "most of these are not actually walled
            // off" is not a tuning report, it is the gate being wrong about what it measures, and
            // there is no threshold that fixes a question this shape.
            //
            // Not replaced with a cleverer version. Enclosure is exactly what null zones express
            // directly, drawn by someone who can see the ward.

            // Far enough from the others that no single dig can turn up two, and so the trail is a
            // route rather than a huddle.
            if (spacing)
            {
                bool crowded = false;
                foreach (var t in taken)
                {
                    if (DigGround.Flat(t, spot) >= DigTuning.RoamSpacing) continue;
                    crowded = true;
                    break;
                }

                if (crowded) { Reject("crowded", spot, ref _rejCrowd); continue; }
            }

            // The wall-versus-hill test. Without it a spot can sit astride a kerb, where half its
            // dig radius is somewhere the player cannot stand.
            if (DigGround.HasStep(spot, DigTuning.RoamDig, DigTuning.SitePieceMaxStep))
            { Reject("step/ledge", spot, ref _rejStep); continue; }

            return true;
        }

        spot = default;
        return false;
    }

    /// <summary>
    /// Throws per spot before giving up. Raised from 120 once sampling moved inside the walkable
    /// box: the gates that now do the rejecting — marker proximity and the walk test — are the
    /// expensive ones, but they only run on candidates that already found ground, which is a far
    /// smaller set than before. A higher budget therefore costs less than it used to and buys the
    /// dense placements the old one could not reach.
    /// </summary>
    private const int PlacementAttempts = 400;

    /// <summary>How close a single-category test spot is buried. Walkable in well under a minute.</summary>
    public const float TestSpotRange = 45f;

    /// <summary>
    /// The reason this test will not run right now, or null when it may.
    ///
    /// <para><b>vnavmesh is a hard requirement of this test, not a preference.</b> There used to be a
    /// "Require navmesh" toggle whose off position fell back to raycast heuristics — and the whole
    /// finding of this session is that those heuristics CANNOT answer reachability even in principle.
    /// A ray finds the first solid surface under a position; the sand outside a housing ward's walls
    /// is solid, it meshes, and it is unreachable. So the toggle's off position did not trade
    /// accuracy for availability, it just produced spots nobody could walk to while looking like a
    /// setting somebody might reasonably pick. Requiring the one component that knows the answer is
    /// simpler to explain and impossible to get wrong.</para>
    ///
    /// <para><b>Scoped to this test, NOT to the plugin.</b> The public build contains none of this —
    /// the entire dig tree is <c>#if DEV_BUILD</c> — so declaring a plugin-wide dependency would make
    /// every player install vnavmesh for a feature they cannot reach.</para>
    /// </summary>
    private static string? RefuseWithoutNavmesh()
    {
        if (!DigNavmesh.Available)
            return "this test REQUIRES vnavmesh and it is not answering. It is the only thing that "
                 + "knows which ground a character can actually reach — a raycast cannot, which is "
                 + "why spots used to land outside the walls. Install or enable vnavmesh.";

        if (!DigNavmesh.Ready)
            return DigNavmesh.StatusLine() + " — wait for it to finish, then try again.";

        if (!DigNavmesh.HasReachableQuery)
            return "this vnavmesh does not publish Query.Mesh.NearestPointReachable, so the gate "
                 + "could only check \"is it on the mesh\" — which accepts disconnected islands and "
                 + "is exactly the hole being closed. Update vnavmesh.";

        return null;
    }

    /// <summary>
    /// Buries ONE spot near the player with a clue pinned to <paramref name="category"/> at
    /// <paramref name="hard"/>, so a single clue category can be judged on its own.
    ///
    /// <para><b>It runs as a real one-stop trail</b> — same HUD, same radar, same dig — rather than
    /// printing a sample clue to chat. A clue that reads well in a chat line and is useless while
    /// actually standing in the zone is precisely the failure this is meant to catch, and only the
    /// real thing catches it.</para>
    ///
    /// <para><b>A category that cannot describe the spot is reported, not papered over.</b> That is
    /// the single most useful outcome of this button: "Enemy produced nothing" means the object table
    /// had no named hostiles loaded near the spot, which is a fact about the category's reach and not
    /// a bug to go hunting for.</para>
    /// </summary>
    public string StartSingle(ClueCategory category, float hard)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return "no character loaded.";

        if (RefuseWithoutNavmesh() is { } refusal) return refusal;

        DigLandmarks.Invalidate();
        DigClueSources.Invalidate();

        _stops.Clear();
        _territory   = Plugin.ClientState.TerritoryType;
        _navRefusals = 0;

        if (!TryPlace(player.Position, new List<Vector3>(), out var spot, nearOnly: TestSpotRange))
            return _navRefusals > 0
                ? "found nowhere walkable within " + (int)TestSpotRange + "y — move somewhere more open."
                : "found nowhere to bury anything within " + (int)TestSpotRange + "y.";

        string clue = new DigClueWriter(_rng).WriteAs(category, spot, hard);

        if (clue.Length == 0)
            return $"{category} had nothing to say about a spot near you — "
                 + $"nothing of that kind is in range. ({DigClueSources.Census(player.Position)})";

        _stops.Add(new Buried { Position = spot, Clue = clue, Category = category });

        _index       = 0;
        _digs        = 0;
        _distance    = float.MaxValue;
        _startedAtMs = Environment.TickCount64;
        _phase       = Phase.Running;
        StartCount++;

        return $"[{category} / {BandName(hard)}] {clue}";
    }

    /// <summary>
    /// The difficulty band a 0..1 value falls in, using the writer's own thresholds.
    /// Named <c>BandName</c> because <see cref="IDigTest.Band"/> already owns <c>Band</c> here — and
    /// that one is the HUD's warmth, an entirely unrelated meaning.
    /// </summary>
    public static string BandName(float hard) => hard switch
    {
        < 0.34f => "EASY",
        < 0.67f => "MEDIUM",
        _       => "HARD",
    };

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

        ResetRejections();

        Rejected.Clear();
        _recordRejects = true;

        // Kept in step with the live zone as well, so anything else in TryPlace that reads the
        // field behaves the same for a sample as it does for a real run. The sampler exists to
        // exercise the REAL placement path; a field only one of its two callers assigns is a
        // difference between them that nothing declares.
        _territory = Plugin.ClientState.TerritoryType;

        // KEEPS GOING PAST A FAILURE, unlike a real run.
        //
        // A trail stops at the first failure because its stops must be a coherent set. The debug
        // sampler must not: breaking made "0 of 200" mean "the FIRST attempt failed", which reads as
        // "nothing can be placed anywhere" and is a far stronger claim than the evidence. Three
        // consecutive failures is the real stop, so a genuinely impossible zone still terminates.
        // NO SPACING RULE HERE. Each sample answers "could a spot land at this point", which is
        // independent of every other sample — so samples may sit on top of one another, and the
        // picture is a map of where placement CAN go rather than one plausible trail. Enforcing the
        // trail's minimum gap is what capped this at roughly 20 of 200: every sample placed made the
        // next one harder, in a ward only a few spacings across.
        //
        // With samples independent there is also nothing to exhaust, so a failure says something
        // about that throw and nothing about the next one. The early-out goes with it: the only
        // limit now is the count asked for.
        for (int i = 0; i < count; i++)
        {
            if (TryPlace(player.Position, found, out var spot, spacing: false))
                found.Add(spot);
        }

        // Off again immediately. A real run must never pay for this, and leaving it on would also
        // let a trail quietly accumulate into the same list.
        _recordRejects = false;

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
        FinishCount++;

        try { Plugin.Sound.Play(SoundService.Cue.ChallengeComplete); }
        catch (Exception ex) { Diag.Error($"[Roam] end cue failed: {ex.Message}"); }

        double seconds = ElapsedSeconds;

        string time = CompletionStore.FormatRaceTime(seconds);
        Plugin.ChatGui.Print($"[Challenges] That's the end of the trail! {time}, {_digs} dig(s).");

        // LAST, and inside its own guard. A listener that throws must not be able to leave the trail
        // half-finished — the phase, the count and the chat line are all already committed above, so
        // the worst a broken subscriber can do is lose its own record.
        try { Finished?.Invoke(seconds, _digs); }
        catch (Exception ex) { Diag.Error($"[Roam] finish listener failed: {ex.Message}"); }
    }

    public string Stop()
    {
        if (_phase == Phase.Off) return "no wild trail is running.";

        bool wasRunning = _phase == Phase.Running;

        _phase = Phase.Off;
        _stops.Clear();

        // Only a RUNNING trail was abandoned. Stopping one that has already reached Done is just the
        // result screen timing out, and announcing that as an abandonment would throw away a run the
        // player actually finished.
        if (wasRunning)
        {
            try { Abandoned?.Invoke(); }
            catch (Exception ex) { Diag.Error($"[Roam] abandon listener failed: {ex.Message}"); }
        }

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

        // The CATEGORY first, because it is the fastest way to read a bad clue: an anchor-based
        // category that points somewhere untrue is a conversion problem, while Quadrant or Clock
        // being wrong cannot be — they use no anchor at all.
        string line = $"[{current.Category}] spot {_index + 1}/{_stops.Count}: "
                    + $"{DigGround.Flat(player.Position, spot):0.0}y {DigGround.Compass(player.Position, spot)} of you";

        if (DigLandmarks.TryMapCoords(spot, out float mx, out float my))
        {
            line += $"   map ({mx:0.0}, {my:0.0})";

            // The numbers the quadrant, the 3x3 cell and the clock bearing are all derived from.
            // Printed because "dead centre" on a spot that is visibly north-east is unanswerable
            // without them — and the cause was the CENTRE being wrong, not the spot.
            // WalkableBox, NOT ContentBounds — they are different functions with different
            // precedence, and the diagnostic was printing the one the CLUE does not use.
            //
            // WalkableBox checks the hand-drawn box first; ContentBounds never looked at it. So with
            // a manual box set, the clue measured its bearings from the hand-drawn centre while this
            // readout confidently printed the navmesh-derived one — and the two disagreed by enough
            // to make a correct clue look wrong. A diagnostic that reads a different source from the
            // thing it is diagnosing is worse than no diagnostic.
            // THE SOURCE COMES BACK FROM WalkableBox ITSELF. A first attempt restated its
            // precedence here as a ?: chain and got the labels arm wrong — it printed "map labels"
            // on a count of four, while WalkableBox also demands their spread exceed five yalms, so
            // a clustered set fell through to the navmesh there and was reported as labels here.
            // That is ContentBounds' defect one size down, so the fix is the same one: ask the
            // chooser what it chose instead of guessing alongside it.
            DigLandmarks.WalkableBox(out var worldLo, out var worldHi, out string source);

            // Converted for display only, since the readout above it is in map coordinates.
            DigLandmarks.TryMapCoords(new Vector3(worldLo.X, 0f, worldLo.Y), out float blx, out float bly);
            DigLandmarks.TryMapCoords(new Vector3(worldHi.X, 0f, worldHi.Y), out float bhx, out float bhy);

            var lo = new Vector2(MathF.Min(blx, bhx), MathF.Min(bly, bhy));
            var hi = new Vector2(MathF.Max(blx, bhx), MathF.Max(bly, bhy));

            line += $"\n[Challenges] walkable box spans x {lo.X:0.0}-{hi.X:0.0}, y {lo.Y:0.0}-{hi.Y:0.0}"
                  + $"   centre ({(lo.X + hi.X) * 0.5f:0.0}, {(lo.Y + hi.Y) * 0.5f:0.0})"
                  + $"   from {source}"
                  + $"   -> {DigLandmarks.Quadrant(spot)}";
        }

        line += $"   world ({spot.X:0.0}, {spot.Y:0.0}, {spot.Z:0.0})";

        // The anchor the clue was written from, and the TRUE relationship to the spot. A clue that
        // says WEST while this says EAST is a conversion bug, not a hard clue.
        // WHICH SOURCE EACH CANDIDATE ANCHOR CAME FROM. "NORTH of Retainer" was unanswerable without
        // this: the name alone does not say whether it came from the map markers, the Aetheryte
        // sheet, the ENpcResident placements or the live object table, and those have completely
        // different fixes. Answering it took a brain query against the running game; it should have
        // taken a glance.
        var here = Plugin.ObjectTable.LocalPlayer?.Position ?? spot;

        line += $"\n[Challenges] anchor sources near the spot — "
              + $"markers {DigClueSources.Landmarks().Count}, "
              + $"aetherytes {DigClueSources.AetherytesNear(spot).Count}, "
              + $"npc placements {DigClueSources.MapNpcs().Count}, "
              + $"live enemies {DigClueSources.NearbyEnemies(here).Count}";

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
    //
    // Lives in DigClues.cs. It moved out of here when it grew from "direction from the nearest
    // landmark" into a weighted draw over seven categories with its own per-trail state — at which
    // point it stopped being a detail of the roam test and became a thing in its own right, with
    // its own sources and its own reason to be read.
}
#endif
