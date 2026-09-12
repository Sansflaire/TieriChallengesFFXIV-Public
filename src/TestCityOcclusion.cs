#if DEV_BUILD
using System;
using System.Numerics;

using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. <b>Occlusion for painted world geometry, by collision raycast.</b> Answers
/// "is the straight line from the camera to this point blocked by the world?" so a building behind a
/// hill or a wall stops showing through it.
///
/// <para><b>Why rays and not the depth buffer.</b> Sansflaire's call after the three routes were costed.
/// FFXIV-TV reaches pixel-perfect occlusion by hooking <c>DrawIndexed</c>/<c>OMSetRenderTargets</c>,
/// identifying the one LDR surface that reaches the backbuffer composite and binding the game's
/// main-scene DSV — its own <c>Golden-Standard-Rendering.md</c> records that taking dozens of
/// versions of diagnostics, and it is a hook into the render pipeline. This needs no hook, no D3D
/// device, no shader and no new dependency: the collision module is already what this plugin uses
/// for ground, and <see cref="DigGround.SegmentClear"/> already asks a line-of-sight question with
/// it.</para>
///
/// <para><b>What it therefore cannot do, stated plainly.</b> Only the COLLISION mesh occludes —
/// terrain, buildings, walls. Characters, foliage, particles and anything else that is drawn but not
/// collidable do not, so a building behind another player still shows through. The edge is blocky at
/// the sample grid's granularity, not per pixel. Both are properties of the route, not bugs to chase:
/// closing them is the depth-buffer route.</para>
///
/// <para><b>Every result is CACHED and refreshed on a per-frame budget.</b> A ray is cheap and 240
/// faces × 9 samples every frame is not — that is over 2,000 rays a frame, sustained, where the dig
/// tests spend that much once at placement. Buildings never move, so a mask is only ever stale while
/// the camera is moving; the budget refreshes the whole visible set in a few frames.</para>
/// </summary>
internal static class TestCityOcclusion
{
    /// <summary>Samples per face: a 3×3 grid, so a face can be partly blocked rather than only on
    /// or off. Nine is the smallest grid with a middle, and the middle is what catches a pillar.</summary>
    public const int Cells = 3;
    public const int CellCount = Cells * Cells;

    /// <summary>Bits per face in a building's packed mask, and the number of faces packed.</summary>
    public const int FaceBits  = CellCount;
    public const int FaceCount = 5;

    /// <summary>
    /// Pulled back from the sampled point before the ray is measured against it. Without it a wall
    /// drawn flush against real geometry — which is most of them, since buildings are placed on the
    /// ground — reads as occluded by the very surface it stands on.
    /// </summary>
    private const float Epsilon = 0.35f;

    /// <summary>A ray this short is not worth firing: the camera is practically on the surface.</summary>
    private const float MinRay = 0.30f;

    /// <summary>
    /// Whether the world blocks the view from <paramref name="eye"/> to <paramref name="point"/>.
    ///
    /// <para>One ray. <c>RaycastMaterialFilter</c> reports the FIRST surface along the direction, so
    /// a hit closer than the point itself — by more than <see cref="Epsilon"/> — means something
    /// stands in between.</para>
    /// </summary>
    public static bool Blocked(Vector3 eye, Vector3 point)
    {
        var delta = point - eye;
        float len = delta.Length();

        if (len < MinRay) return false;

        var dir = delta / len;

        if (!BGCollisionModule.RaycastMaterialFilter(eye, dir, out var hit, len))
            return false;                                  // nothing at all in the way

        float travelled = Vector3.Distance(eye, new Vector3(hit.Point.X, hit.Point.Y, hit.Point.Z));

        return travelled < len - Epsilon;
    }

    /// <summary>
    /// Tests one face's 3×3 grid and returns the visibility bits, LSB = cell 0.
    ///
    /// <para>The quad is given by its four corners and sampled at each cell's CENTRE — a cell whose
    /// centre is visible is drawn whole. Sampling corners instead would make neighbouring cells
    /// disagree about a shared point, and the seam between them would flicker.</para>
    /// </summary>
    public static uint TestFace(Vector3 eye, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
    {
        uint bits = 0;

        for (int r = 0; r < Cells; r++)
        {
            float v = (r + 0.5f) / Cells;

            for (int c = 0; c < Cells; c++)
            {
                float u = (c + 0.5f) / Cells;

                var mid = Bilinear(p0, p1, p2, p3, u, v);

                if (!Blocked(eye, mid)) bits |= 1u << (r * Cells + c);
            }
        }

        return bits;
    }

    /// <summary>The cell a point in a face's own <c>(u, v)</c> frame falls in — so a window can ask
    /// whether its patch of wall is visible without costing another ray.</summary>
    public static bool CellVisible(uint bits, float u, float v)
    {
        int c = Math.Clamp((int)(u * Cells), 0, Cells - 1);
        int r = Math.Clamp((int)(v * Cells), 0, Cells - 1);

        return (bits & (1u << (r * Cells + c))) != 0;
    }

    public static bool AllVisible(uint bits) => (bits & FullMask) == FullMask;

    public static bool NoneVisible(uint bits) => (bits & FullMask) == 0;

    /// <summary>All nine cells visible. The starting value for an untested face — a fresh city draws
    /// whole and then resolves, which is far better than flashing empty for the first few frames.</summary>
    public const uint FullMask = (1u << CellCount) - 1;

    /// <summary>Reads one face's bits out of a building's packed mask.</summary>
    public static uint FaceBitsOf(ulong packed, int face) =>
        (uint)((packed >> (face * FaceBits)) & FullMask);

    /// <summary>Writes one face's bits into a building's packed mask.</summary>
    public static ulong WithFace(ulong packed, int face, uint bits)
    {
        int shift = face * FaceBits;
        packed &= ~((ulong)FullMask << shift);
        return packed | ((ulong)(bits & FullMask) << shift);
    }

    /// <summary>Every face of every building visible — what a mask is initialised to.</summary>
    public static ulong AllFacesVisible()
    {
        ulong packed = 0;
        for (int f = 0; f < FaceCount; f++) packed = WithFace(packed, f, FullMask);
        return packed;
    }

    private static Vector3 Bilinear(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u, float v)
        => Vector3.Lerp(Vector3.Lerp(p0, p1, u), Vector3.Lerp(p3, p2, u), v);
}
#endif
