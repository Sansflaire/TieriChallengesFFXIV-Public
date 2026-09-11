#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace TieriChallengesFFXIV;

/// <summary>How warm the player is. Drives the HUD colour for every test that has a target.</summary>
internal enum DigBand { Cold, Red, Yellow, Green, Dig }

/// <summary>
/// DEVELOPER BUILD ONLY. What every dig test looks like from the outside, so the HUD and the lab
/// window can drive all three without knowing which one is running.
///
/// <para>Exactly one test may be active at a time — <see cref="DigTests"/> enforces it. Two running
/// at once would put two headlines in the same HUD slot and make "which spot did that dig mean"
/// unanswerable.</para>
/// </summary>
internal interface IDigTest
{
    /// <summary>Name as it appears in the lab and in chat.</summary>
    string Name { get; }

    bool IsActive { get; }

    /// <summary>The big word on the HUD.</summary>
    string Headline { get; }

    /// <summary>The small line under it.</summary>
    string Subtitle { get; }

    DigBand Band { get; }

    double ElapsedSeconds { get; }

    /// <summary>
    /// Null when this test has no radar. Otherwise 0 (just in range) to 1 (on the spot) — the
    /// pulsing ring the Clue Trail uses.
    /// </summary>
    float? RadarCloseness { get; }

    string Start();

    /// <summary>
    /// Begins a dig. <b>Does not award anything</b> — it decides what the dig WOULD find and holds
    /// it until <see cref="DigFinished"/> says the animation actually played.
    /// </summary>
    string Dig();

    /// <summary>
    /// The dig animation ended. <paramref name="completed"/> is false when it was cut short.
    ///
    /// <para>A find is only banked on a completed dig: the reward is for doing the work, and an
    /// interrupted dig did not do it. Judging the hit at dig time and applying it here is safe
    /// because input is blocked for the duration, so the player cannot have moved in between.</para>
    /// </summary>
    void DigFinished(bool completed);

    string Stop();
    void   Tick();

    /// <summary>In-world drawing. A no-op for tests with nothing to show.</summary>
    void DrawWorld();
}

/// <summary>
/// DEVELOPER BUILD ONLY. Finds somewhere real to bury something.
///
/// <para><b>"A random valid location" is honestly "a random location that collision says has ground
/// under it, at a height you could plausibly walk to".</b> There is no cheap oracle for the walkable
/// area of a territory — the navmesh that would answer it properly lives in vnavmesh, and this
/// plugin does not depend on a sibling. So this is rejection sampling: throw a point, drop a ray on
/// it, keep it if the ray finds ground near the player's own height. That reliably lands somewhere
/// real; it does not guarantee a route exists to it, and in broken terrain it can pick the far side
/// of a wall. Wiring in vnavmesh's <c>Query.Mesh</c> IPC is the upgrade if that ever bites.</para>
/// </summary>
internal static class DigGround
{
    /// <summary>Start the probe ray this far above the reference point, and fire it this far down.</summary>
    private const float RayAbove  = 120f;
    private const float RayLength = 400f;

    /// <summary>Give up after this many throws rather than searching forever in a sealed room.</summary>
    public const int DefaultAttempts = 240;

    /// <summary>
    /// A point on the ground between <paramref name="min"/> and <paramref name="max"/> yalms from
    /// <paramref name="from"/>, on a random bearing.
    /// </summary>
    public static bool TryPointNear(Vector3 from, float min, float max, Random rng,
                                    out Vector3 point, int attempts = DefaultAttempts)
    {
        point = default;
        if (max <= min) max = min + 1f;

        for (int i = 0; i < attempts; i++)
        {
            float angle = (float)rng.NextDouble() * MathF.Tau;

            // Uniform over the annulus, not over the radius — sampling the radius directly bunches
            // spots against the inner edge, where they are the least interesting to look for.
            float u    = (float)rng.NextDouble();
            float dist = MathF.Sqrt(min * min + u * (max * max - min * min));

            if (TryGround(from, from.X + dist * MathF.Sin(angle), from.Z + dist * MathF.Cos(angle),
                          out point))
                return true;
        }

        return false;
    }

    /// <summary>A point on the ground somewhere inside a box volume.</summary>
    public static bool TryPointInBox(ChallengeArea box, Random rng, out Vector3 point,
                                     int attempts = DefaultAttempts)
    {
        point = default;

        float hx  = MathF.Max(0.01f, box.SizeX * box.Scale) * 0.5f;
        float hz  = MathF.Max(0.01f, box.SizeZ * box.Scale) * 0.5f;
        float cos = MathF.Cos(box.RotationY);
        float sin = MathF.Sin(box.RotationY);

        // Inset so nothing is buried in the wall itself, where half the dig radius falls outside
        // the volume the player is told they may search.
        const float Inset = 0.9f;

        for (int i = 0; i < attempts; i++)
        {
            float lx = ((float)rng.NextDouble() * 2f - 1f) * hx * Inset;
            float lz = ((float)rng.NextDouble() * 2f - 1f) * hz * Inset;

            float wx = box.X + lx * cos - lz * sin;
            float wz = box.Z + lx * sin + lz * cos;

            if (TryGround(box.Center, wx, wz, out point)) return true;
        }

        return false;
    }

    /// <summary>
    /// Ground height at an XZ position, for DRAWING rather than for placement. Unlike
    /// <see cref="TryPointNear"/> it applies no reachability rule and never fails: a ray that finds
    /// nothing falls back to the reference height, because a grid vertex at a slightly wrong height
    /// is far better than a hole in the grid.
    /// </summary>
    public static Vector3 GroundAt(Vector3 reference, float x, float z)
    {
        var origin = new Vector3(x, reference.Y + RayAbove, z);

        if (BGCollisionModule.RaycastMaterialFilter(origin, new Vector3(0f, -1f, 0f),
                                                    out var hit, RayLength))
            return new Vector3(x, hit.Point.Y, z);

        return new Vector3(x, reference.Y, z);
    }

    private static bool TryGround(Vector3 reference, float x, float z, out Vector3 point)
    {
        point = default;

        var origin = new Vector3(x, reference.Y + RayAbove, z);

        if (!BGCollisionModule.RaycastMaterialFilter(origin, new Vector3(0f, -1f, 0f),
                                                     out var hit, RayLength))
            return false;

        if (MathF.Abs(hit.Point.Y - reference.Y) > DigTuning.MaxRise) return false;

        point = new Vector3(x, hit.Point.Y, z);
        return true;
    }

    /// <summary>
    /// Ground at an XZ position anywhere on the map, with the height gate as a PARAMETER rather
    /// than always measured against the player.
    ///
    /// <para><b>The player-relative rise gate cannot survive whole-map sampling.</b> It exists to
    /// reject clifftops and rooftops near you, and across a whole zone it would instead reject every
    /// legitimate hill, basement and terrace merely for being far from where you happen to stand.
    /// Pass 0 to disable it and use <see cref="IsElevated"/> for the roof question instead, which
    /// asks something local and therefore stays true anywhere.</para>
    /// </summary>
    public static bool TryGroundAt(Vector3 reference, float x, float z, float maxRise,
                                   out Vector3 point)
    {
        point = default;

        var origin = new Vector3(x, reference.Y + RayAbove, z);

        if (!BGCollisionModule.RaycastMaterialFilter(origin, new Vector3(0f, -1f, 0f),
                                                     out var hit, RayLength))
            return false;

        if (maxRise > 0f && MathF.Abs(hit.Point.Y - reference.Y) > maxRise) return false;

        point = new Vector3(x, hit.Point.Y, z);
        return true;
    }

    /// <summary>
    /// Ground found by dropping a SHORT ray from just above a known-good point, rather than from
    /// the sky.
    ///
    /// <para><b>Grounding from above is how spots ended up on rooftops.</b> The normal probe starts
    /// 120 yalms up and takes the first surface it meets — which, over a house, is the roof. That is
    /// correct when all you have is an XZ guess. It is catastrophic when you already know the
    /// correct height, because it throws that knowledge away and re-derives a worse answer: a
    /// navmesh point on the path outside a house, re-grounded from the sky, becomes a point on the
    /// roof of the house.</para>
    ///
    /// <para>So this starts just above the point it was given and only looks a little way down. It
    /// refines a height that is already nearly right; it cannot relocate the spot onto something
    /// else entirely.</para>
    /// </summary>
    public static bool TryGroundBelow(Vector3 point, float above, float length, out Vector3 ground)
    {
        ground = point;

        var origin = new Vector3(point.X, point.Y + above, point.Z);

        if (!BGCollisionModule.RaycastMaterialFilter(origin, new Vector3(0f, -1f, 0f),
                                                     out var hit, length))
            return false;

        ground = new Vector3(point.X, hit.Point.Y, point.Z);
        return true;
    }

    /// <summary>
    /// Whether a point is standing on something with more ground a long way UNDER it — a roof, a
    /// balcony, a bridge, the top of a wall.
    ///
    /// <para><b>This is the roof test that works anywhere</b>, and it replaces "is it far above the
    /// player" which only worked nearby. It fires a second ray from just below the surface: if it
    /// lands again with a big gap, the first surface was an elevated platform with open space under
    /// it. Real terrain has nothing beneath it to find.</para>
    ///
    /// <para>It is not infallible — a solid stone plinth has no void under it and reads as terrain,
    /// which is the right answer anyway, while a ground-floor room inside a building has the world
    /// below it and can read as elevated. It rejects the cases that matter in a no-flying zone,
    /// which is roofs and the tops of walls.</para>
    /// </summary>
    public static bool IsElevated(Vector3 point, float clearance)
    {
        if (clearance <= 0f) return false;

        var origin = new Vector3(point.X, point.Y - 0.35f, point.Z);

        if (!BGCollisionModule.RaycastMaterialFilter(origin, new Vector3(0f, -1f, 0f),
                                                     out var hit, RayLength))
            return false;

        return point.Y - hit.Point.Y > clearance;
    }

    /// <summary>Spokes and spacing for the step test. Eight directions catch any wall that crosses
    /// the circle; only a wall clipping a corner between spokes can slip past.</summary>
    private const int   StepRays       = 8;
    private const float StepSpacing    = 0.5f;
    private const int   MaxStepSamples = 16;

    /// <summary>
    /// Whether the ground within <paramref name="radius"/> of a point contains a STEP — a wall, a
    /// ledge, a kerb — as opposed to a slope.
    ///
    /// <para><b>Total height cannot tell those apart and this can.</b> A 2-yalm rise across a
    /// 4-yalm radius is a 26° hillside and fine to dig on; a 1-yalm garden wall is a <i>smaller</i>
    /// total rise and completely unusable. The difference is not how far the ground moves but how
    /// suddenly, so this walks outward in short increments and looks at the change between
    /// neighbours rather than the spread across the circle.</para>
    ///
    /// <para><b>It has to raycast, and a cached lattice cannot substitute.</b> Lattice samples are
    /// a couple of yalms apart and read back with bilinear interpolation, which turns a one-yalm
    /// wall into a gentle ramp — the smoothing that makes a drawn grid look good is exactly what
    /// destroys the evidence here.</para>
    /// </summary>
    public static bool HasStep(Vector3 centre, float radius, float maxStep)
    {
        float r   = MathF.Max(0.5f, radius);
        float max = MathF.Max(0.02f, maxStep);

        int   steps   = Math.Clamp((int)MathF.Ceiling(r / StepSpacing), 2, MaxStepSamples);
        float centreY = GroundAt(centre, centre.X, centre.Z).Y;

        for (int d = 0; d < StepRays; d++)
        {
            float a   = MathF.Tau * d / StepRays;
            float cos = MathF.Cos(a), sin = MathF.Sin(a);

            float prev = centreY;

            for (int k = 1; k <= steps; k++)
            {
                float rr = r * k / steps;
                float y  = GroundAt(centre, centre.X + rr * cos, centre.Z + rr * sin).Y;

                if (MathF.Abs(y - prev) > max) return true;
                prev = y;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a straight walk from <paramref name="a"/> to <paramref name="b"/> is blocked by solid
    /// geometry at body height.
    ///
    /// <para><b>This exists because vnavmesh returns routes through solid walls and I do not know
    /// why.</b> Observed: standing in a sealed alcove of the Empyreum apartment building — STATIC
    /// zone geometry, not a player-placed object — <c>Nav.Pathfind</c> returned a three-waypoint,
    /// 21-yalm route at 1.2x the straight-line distance, straight through the stone. Two
    /// explanations were offered for this class of failure before it was understood and both were
    /// wrong, so no third one is offered here.</para>
    ///
    /// <para><b>The point is that this check does not need the explanation.</b> The collision system
    /// is what actually stops the character — it knows about the wall, because the wall is what the
    /// player is standing against. Whatever the navmesh believes, a segment that hits collision is
    /// not walkable, and that holds whether the cause is a stale mesh, an unbuilt tile, geometry
    /// vnavmesh excludes, or something not yet guessed at.</para>
    ///
    /// <para><b>It is a line-of-walk test, not a reachability proof.</b> A spot round a corner is
    /// perfectly reachable and fails this; a spot behind a wall is unreachable and also fails it.
    /// So it is only sound as a NEGATIVE check on a route the navmesh already proposed — each
    /// segment of a navmesh path is meant to be a straight walkable run, so a segment that hits a
    /// wall means that path is fiction.</para>
    /// </summary>
    public static bool SegmentClear(Vector3 a, Vector3 b, float height = 1.0f)
    {
        float len = Flat(a, b);
        if (len < 0.05f) return true;

        // A SINGLE STRAIGHT RAY AT FIXED HEIGHT WAS WRONG, and wrong in the direction that matters:
        // it reported 46 of 57 spots blocked, including ones on open paving. A ray from A to B holds
        // its altitude while the ground does not, so every slope, stair and gentle rise between two
        // points intercepts it. It could not tell a hill from a wall, which is the only distinction
        // being asked for.
        //
        // So walk the line instead, sampling the GROUND at each step. A slope moves the ground a
        // little per step; a wall moves it a lot, or removes it entirely. That is the same reasoning
        // HasStep already uses radially, applied along a line — and it is the reasoning that
        // distinguishes terrain from obstruction.
        int steps = Math.Clamp((int)MathF.Ceiling(len / StepLength), 2, MaxWalkSamples);

        // Start from the real ground under A rather than from A itself: a waypoint can sit slightly
        // above or below the floor, and inheriting that error biases the first comparison.
        if (!TryGroundBelow(a, 2f, 8f, out var cursor)) cursor = a;

        for (int k = 1; k <= steps; k++)
        {
            float t = (float)k / steps;

            float x = a.X + (b.X - a.X) * t;
            float z = a.Z + (b.Z - a.Z) * t;

            // Probed from just above the PREVIOUS ground height, so the ray tracks the surface as it
            // rises and falls. Probing from a fixed altitude would find rooftops on the way past.
            var probe = new Vector3(x, cursor.Y, z);

            if (!TryGroundBelow(probe, WalkProbeUp, WalkProbeDown, out var ground))
                return false;   // nothing under this step at all — a gap, a ledge or a wall face

            if (MathF.Abs(ground.Y - cursor.Y) > MathF.Max(0.05f, DigTuning.WalkMaxStep))
                return false;   // too abrupt to walk

            // AND A SHORT HORIZONTAL RAY, because a downward ray cannot see a vertical wall.
            //
            // This is what the ground walk alone could never catch, and it is why a spot behind the
            // Empyreum apartment facade still verified as walkable. A building sits ON the terrain:
            // the floor runs underneath it, so sampling the ground finds a similar height on both
            // sides of the wall and the walk strolls through the stone. Ground sampling detects
            // steps and gaps. It is blind, by construction, to a vertical face with floor either
            // side of it.
            //
            // The very first version of this check was a horizontal ray and was abandoned because it
            // held its altitude while the ground did not. That objection dies at this length: over
            // one 1.2-yalm stride, between two points already known to be on the ground and within a
            // stride of each other, there is no altitude to drift. The two tests are complements —
            // the walk follows the terrain, the ray sees what stands on it.
            if (!StrideClear(cursor, ground, height)) return false;

            cursor = ground;
        }

        return true;
    }

    /// <summary>
    /// Whether anything solid stands between two adjacent ground samples, checked at two heights.
    ///
    /// <para>Two heights because one misses in both directions: a knee-high ray clears a railing a
    /// character cannot vault, and a chest-high one passes over a low wall it also cannot cross.</para>
    /// </summary>
    private static bool StrideClear(Vector3 fromGround, Vector3 toGround, float height)
    {
        foreach (float h in new[] { 0.45f, height })
        {
            var from = new Vector3(fromGround.X, fromGround.Y + h, fromGround.Z);
            var to   = new Vector3(toGround.X,   toGround.Y   + h, toGround.Z);

            var delta = to - from;
            float len = delta.Length();

            if (len < 0.02f) continue;

            if (BGCollisionModule.RaycastMaterialFilter(from, delta / len, out _, len))
                return false;
        }

        return true;
    }

    /// <summary>Spacing of the walk samples, and the ceiling on how many a long segment may take.</summary>
    private const float StepLength     = 1.2f;
    private const int   MaxWalkSamples = 96;

    /// <summary>
    /// How far up and down each step looks for ground. Up is generous enough to clear a normal stair
    /// riser; down is bounded so a step out over a drop reads as a gap rather than finding the
    /// courtyard below and calling it continuous.
    /// </summary>
    private const float WalkProbeUp   = 1.6f;
    private const float WalkProbeDown = 4.0f;

    // The stride height lives in DigTuning.WalkMaxStep — it decides over- versus under-rejection,
    // and that is a feel found by trying it rather than a fact derivable from here.

    /// <summary>
    /// Whether every segment of a proposed route is clear of solid geometry. A navmesh path whose
    /// segments cut through walls is not a route, however confidently it was returned.
    /// </summary>
    public static bool RouteClear(Vector3 start, IReadOnlyList<Vector3> waypoints, out int blockedAt)
    {
        blockedAt = -1;

        var prev = start;

        for (int i = 0; i < waypoints.Count; i++)
        {
            if (!SegmentClear(prev, waypoints[i])) { blockedAt = i; return false; }
            prev = waypoints[i];
        }

        return true;
    }

    /// <summary>Distance on the XZ plane. Height is bounded at placement, so it never matters here.</summary>
    public static float Flat(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Eight-point compass bearing in world terms, not relative to the player's facing — "north"
    /// has to mean the same thing every time it is said, or the reading is useless the moment you
    /// turn around.
    ///
    /// <para>FFXIV's world axes: <b>+X is east and +Z is south</b>, so north is <c>-Z</c>. Not a
    /// guess — the game's own world-to-map conversion in <c>scripts/gen-datasets</c> maps world Z
    /// onto the map's Y, and map Y increases downward, which is south.</para>
    /// </summary>
    public static string Compass(Vector3 from, Vector3 to)
    {
        float east  = to.X - from.X;
        float north = from.Z - to.Z;      // north is -Z, hence the reversed subtraction

        float bearing = MathF.Atan2(east, north) * 180f / MathF.PI;
        if (bearing < 0f) bearing += 360f;

        return Points[(int)MathF.Round(bearing / 45f) % 8];
    }

    /// <summary>
    /// FOUR-point bearing — the coarse version, for the hard end of a difficulty band.
    ///
    /// <para>A quarter of the compass instead of an eighth. "EAST" covering 90 degrees rather than
    /// 45 is genuinely less information while still being true, which is what degrading a fact
    /// means: the same kind of statement, said less precisely. Saying something vaguer is not the
    /// same as saying something woollier, and this is the former.</para>
    /// </summary>
    public static string Compass4(Vector3 from, Vector3 to)
    {
        float east  = to.X - from.X;
        float north = from.Z - to.Z;

        float bearing = MathF.Atan2(east, north) * 180f / MathF.PI;
        if (bearing < 0f) bearing += 360f;

        return Quarters[(int)MathF.Round(bearing / 90f) % 4];
    }

    private static readonly string[] Quarters = { "NORTH", "EAST", "SOUTH", "WEST" };

    private static readonly string[] Points =
        { "NORTH", "NORTH-EAST", "EAST", "SOUTH-EAST", "SOUTH", "SOUTH-WEST", "WEST", "NORTH-WEST" };

    /// <summary>
    /// A deliberately woolly distance word. A bearing alone leaves a 220-yalm hunt as a very long
    /// walk in a straight line, but a number would give the whole thing away — so, four steps.
    /// </summary>
    public static string Vagueness(float d) => d switch
    {
        >= 180f => "a long way off",
        >= 100f => "far away",
        >= 45f  => "some way off",
        _       => "close by",
    };
}
#endif
