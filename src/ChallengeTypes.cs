using System;
using System.Numerics;

namespace TieriChallengesFFXIV;

/// <summary>
/// What kind of condition completes a challenge. Persisted as an int — NEVER renumber an
/// existing member, only append. Old configs store the raw value.
/// </summary>
/// <summary>
/// How the challenge list is ordered. Persisted as an int — append, never renumber.
/// </summary>
public enum ChallengeSort
{
    /// <summary>Creation order: the order challenges were authored in. The default.</summary>
    Created      = 0,

    /// <summary>Title, A→Z, case-insensitive.</summary>
    Alphabetical = 1,

    /// <summary>Easiest first, unrated last, tie-broken by the last plain order chosen.</summary>
    Difficulty   = 2,
}

public enum ChallengeKind
{
    /// <summary>Hand-toggled. The original behaviour and the default for built-ins.</summary>
    Manual = 0,

    /// <summary>Enter every area at least once, any order, within one login session.</summary>
    VisitAreas = 1,

    /// <summary>Enter every area in the listed order, within one login session.</summary>
    VisitAreasInOrder = 2,

    /// <summary>Perform a specific emote inside the (single) area, optionally facing a captured direction.</summary>
    EmoteAtArea = 3,

    /// <summary>Be mounted on a specific mount while inside the area.</summary>
    MountInArea = 5,

    /// <summary>
    /// Be in a zone (and optionally inside an area within it) while wearing either a complete
    /// Glamour Dresser outfit or one specific piece of gear / weapon.
    /// </summary>
    GearInArea = 6,

    /// <summary>
    /// <b>The composite kind, and the only one the Creator offers.</b> One or more areas
    /// (<see cref="CustomChallenge.Mode"/> decides how many and whether order matters), each
    /// carrying its own list of <see cref="ChallengeCondition"/>s that must all hold while the
    /// player is inside it.
    ///
    /// <para>Kinds 1–6 above are LEGACY. They keep their detectors and keep firing forever, but
    /// nothing new is authored as one. They are deliberately NOT migrated to this kind: doing so
    /// would raise each challenge's <see cref="ChallengeCatalog.RequiredFor"/> baseline and delete
    /// every already-published challenge from the list of anyone who has not updated yet.</para>
    /// </summary>
    InArea = 7,

    /// <summary>
    /// A timed run between two volumes. The player arms it by standing in the start area, starts
    /// it deliberately, and completes it by reaching the finish area before the fail time — while
    /// staying inside an optional bounding "quit" area.
    ///
    /// <para>Its own kind rather than a composite condition because it is the only thing in the
    /// plugin with a STATE MACHINE. Every other challenge is a pure function of the current frame:
    /// stand in the right place under the right conditions and it completes, and nothing is
    /// remembered between ticks except which stops are already done. A race has a running clock, a
    /// deliberate start, and three distinct ways to end — none of which a stateless condition can
    /// express.</para>
    /// </summary>
    RaceTimer = 8,
}

/// <summary>Why a race stopped. Drives the message the player sees.</summary>
public enum RaceOutcome
{
    /// <summary>Reached the finish area in time.</summary>
    Finished = 0,

    /// <summary>Ran out of time.</summary>
    TimedOut = 1,

    /// <summary>Left the bounding area.</summary>
    LeftArea = 2,

    /// <summary>Gave up, or the run was interrupted (logout, zone change).</summary>
    Abandoned = 3,
}

/// <summary>What <see cref="ChallengeKind.GearInArea"/> requires the player to be wearing.</summary>
public enum GearRequirement
{
    /// <summary>Every defined slot of a MirageStoreSetItem outfit.</summary>
    FullOutfit = 0,

    /// <summary>One specific item in any slot — a weapon, a hat, whatever.</summary>
    SingleItem = 1,
}

public enum AreaShape
{
    Sphere = 0,
    Box    = 1,
}

/// <summary>
/// One trigger volume, in world coordinates. Authored by standing somewhere and capturing the
/// player position, then nudged/resized in the creator.
///
/// <para><b>Scale</b> multiplies the authored dimensions. It exists so a volume can be grown or
/// shrunk uniformly without losing the numbers that were dialled in — set Radius/Size once,
/// then tune Scale.</para>
/// </summary>
[Serializable]
public sealed class ChallengeArea
{
    public string Name { get; set; } = "Area";

    public AreaShape Shape { get; set; } = AreaShape.Sphere;

    // Centre, world space. Y is vertical.
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    /// <summary>Sphere radius in yalms, before <see cref="Scale"/>.</summary>
    public float Radius { get; set; } = 3f;

    /// <summary>Box full extents in yalms, before <see cref="Scale"/>.</summary>
    public float SizeX { get; set; } = 6f;
    public float SizeY { get; set; } = 6f;
    public float SizeZ { get; set; } = 6f;

    /// <summary>Uniform multiplier over Radius / Size*.</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Box yaw in radians. Ignored for spheres.</summary>
    public float RotationY { get; set; }

    /// <summary>
    /// The Map row this position belongs to, captured when the centre was taken from the player.
    /// 0 = never captured (an area authored before this field existed).
    ///
    /// <para><b>Why a position needs a map id at all.</b> One territory can present several maps.
    /// A residential district has a ward map AND a subdivision map with completely different
    /// coordinate offsets — Empyreum's are (0, 0) and (702, 655) — and <c>TerritoryType.Map</c>
    /// names only the first. Flagging a subdivision position against the ward map puts the marker
    /// off the edge of the map entirely, which is exactly what shipped in 0.81.36.0.</para>
    ///
    /// <para>Captured at authoring rather than derived later, for the same reason
    /// <see cref="CustomChallenge.TerritoryId"/> is: the one moment the answer is certainly right
    /// is the moment the author is standing on the spot.</para>
    /// </summary>
    public uint MapId { get; set; }

    public Vector3 Center => new(X, Y, Z);

    public void SetCenter(Vector3 v) { X = v.X; Y = v.Y; Z = v.Z; }

    /// <summary>Effective sphere radius after scale.</summary>
    public float EffectiveRadius => MathF.Max(0.01f, Radius * Scale);

    /// <summary>
    /// Smallest half-dimension of the volume. Used to choose how finely to sample movement: a
    /// volume narrower than the distance travelled between ticks can otherwise be jumped clean
    /// over without ever registering.
    /// </summary>
    public float MinExtent => Shape == AreaShape.Sphere
        ? EffectiveRadius
        : MathF.Max(0.01f, MathF.Min(MathF.Min(SizeX, SizeY), SizeZ) * Scale * 0.5f);

    /// <summary>
    /// Point-in-volume test. Hot path: called for every active area on every tracker tick, so
    /// it is branch-light, allocation-free, and uses squared distance for spheres.
    /// </summary>
    public bool Contains(Vector3 p)
    {
        float dx = p.X - X;
        float dy = p.Y - Y;
        float dz = p.Z - Z;

        if (Shape == AreaShape.Sphere)
        {
            float r = EffectiveRadius;
            return (dx * dx + dy * dy + dz * dz) <= r * r;
        }

        // Box: rotate the delta into the box's local frame (inverse yaw), then slab-test.
        float hx = MathF.Max(0.01f, SizeX * Scale) * 0.5f;
        float hy = MathF.Max(0.01f, SizeY * Scale) * 0.5f;
        float hz = MathF.Max(0.01f, SizeZ * Scale) * 0.5f;

        if (RotationY != 0f)
        {
            float cos = MathF.Cos(-RotationY);
            float sin = MathF.Sin(-RotationY);
            float lx  = dx * cos - dz * sin;
            float lz  = dx * sin + dz * cos;
            dx = lx;
            dz = lz;
        }

        return MathF.Abs(dx) <= hx && MathF.Abs(dy) <= hy && MathF.Abs(dz) <= hz;
    }

    public ChallengeArea Clone() => new()
    {
        Name = Name, Shape = Shape,
        X = X, Y = Y, Z = Z,
        Radius = Radius, SizeX = SizeX, SizeY = SizeY, SizeZ = SizeZ,
        Scale = Scale, RotationY = RotationY, MapId = MapId,
    };

    public string Describe() => Shape == AreaShape.Sphere
        ? $"sphere r={EffectiveRadius:0.##}"
        : $"box {SizeX * Scale:0.##}×{SizeY * Scale:0.##}×{SizeZ * Scale:0.##}";
}

/// <summary>Angle helpers for the facing requirement.</summary>
public static class Facing
{
    /// <summary>
    /// Smallest absolute difference between two angles in radians, result in [0, π].
    /// Handles the wrap at ±π, which a naive subtraction gets wrong for exactly the case that
    /// matters (facing near due south, where the game's rotation flips sign).
    /// </summary>
    public static float AbsDelta(float a, float b)
    {
        float d = a - b;
        while (d >  MathF.PI) d -= MathF.Tau;
        while (d < -MathF.PI) d += MathF.Tau;
        return MathF.Abs(d);
    }

    public static float ToDegrees(float radians) => radians * 180f / MathF.PI;
    public static float ToRadians(float degrees) => degrees * MathF.PI / 180f;
}
