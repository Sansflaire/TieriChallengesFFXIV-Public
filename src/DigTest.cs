#if DEV_BUILD
using System;
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
