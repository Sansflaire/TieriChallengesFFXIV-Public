#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. <b>The shape of a building, in one place.</b> Both renderers read these —
/// the painted one to lay out screen quads, the D3D one to emit world triangles.
///
/// <para><b>Why the numbers live here rather than in each renderer.</b> Two copies of "a window is
/// 52% of its cell and sits between 32% and 78% of its floor" is two facades that agree until
/// somebody tunes one. This is the same rule that deleted the "width the pills take" constant and
/// that keeps <see cref="TestCityService.TileCorner"/> the only source of tile geometry: one copy,
/// many consumers.</para>
/// </summary>
internal static class TestCityFacade
{
    /// <summary>Roughly one window column per this many yalms of wall.</summary>
    public const float WindowPitch = 2.4f;

    /// <summary>Fraction of its cell a window fills across, and where in its floor it sits.</summary>
    public const float WindowWidthFrac = 0.52f;
    public const float WindowTopFrac   = 0.78f;
    public const float WindowBotFrac   = 0.32f;

    public const int MaxWindowCols = 10;
    public const int MaxWindowRows = 60;

    public const float DoorWidth  = 1.4f;
    public const float DoorHeight = 2.4f;

    /// <summary>
    /// A fixed light. A direction rather than a real sun: the point is that the four walls and the
    /// roof come out at five different brightnesses, and a light that tracked Eorzean time would make
    /// a building's north face change identity while you walked round it.
    /// </summary>
    public static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.42f, 0.82f, -0.39f));

    /// <summary>Flat directional shading, floored well above zero so a wall facing away from the
    /// light is dim rather than black — five readable brightnesses, not one plus a silhouette.</summary>
    public static float Shade(Vector3 normal) =>
        0.52f + 0.48f * MathF.Max(0f, Vector3.Dot(normal, Sun));

    /// <summary>The four walls, as (first corner, second corner, outward normal).
    ///
    /// <para>Normals are written down rather than derived: a footprint is axis-aligned by
    /// construction, so there is nothing to compute and nothing to get the sign of wrong.</para>
    /// </summary>
    public static (Vector2 A, Vector2 B, Vector3 N) Wall(TestCityService city, CityBuilding b,
                                                         CitySide side)
    {
        int i0 = b.TileX;
        int j0 = b.TileZ;
        int i1 = b.TileX + Math.Max(1, b.TilesX);
        int j1 = b.TileZ + Math.Max(1, b.TilesZ);

        var c00 = city.TileCorner(i0, j0);
        var c10 = city.TileCorner(i1, j0);
        var c11 = city.TileCorner(i1, j1);
        var c01 = city.TileCorner(i0, j1);

        return side switch
        {
            CitySide.NegZ => (c00, c10, new Vector3(0f, 0f, -1f)),
            CitySide.PosX => (c10, c11, new Vector3(1f, 0f,  0f)),
            CitySide.PosZ => (c11, c01, new Vector3(0f, 0f,  1f)),
            _             => (c01, c00, new Vector3(-1f, 0f, 0f)),
        };
    }

    /// <summary>The four top corners, in ring order.</summary>
    public static (Vector2 A, Vector2 B, Vector2 C, Vector2 D) Footprint(TestCityService city,
                                                                         CityBuilding b)
    {
        int i0 = b.TileX;
        int j0 = b.TileZ;
        int i1 = b.TileX + Math.Max(1, b.TilesX);
        int j1 = b.TileZ + Math.Max(1, b.TilesZ);

        return (city.TileCorner(i0, j0), city.TileCorner(i1, j0),
                city.TileCorner(i1, j1), city.TileCorner(i0, j1));
    }

    /// <summary>Window columns for a wall of a given length.</summary>
    public static int Columns(float wallLength) =>
        Math.Clamp((int)MathF.Floor(wallLength / WindowPitch), 1, MaxWindowCols);

    /// <summary>
    /// Whether one window is lit. <b>Deterministic per window</b> — a per-frame roll would strobe
    /// the whole city, which reads as a rendering fault. Nothing cryptographic: a multiply-xor mix,
    /// well spread enough that neighbours differ.
    /// </summary>
    public static bool Lit(int seed, int floor, int col, int percent)
    {
        unchecked
        {
            uint h = (uint)seed;
            h = (h ^ (uint)(floor * 0x9E3779B9)) * 0x85EBCA6B;
            h = (h ^ (uint)(col   * 0x27D4EB2F)) * 0xC2B2AE35;
            h ^= h >> 15;
            return h % 100u < (uint)percent;
        }
    }
}

/// <summary>
/// DEVELOPER BUILD ONLY. Turns the city into a vertex buffer for <see cref="TestCityD3D"/>.
///
/// <para><b>Triangles first, then lines, in ONE buffer.</b> The renderer draws
/// <c>[0, triangleVertexCount)</c> as a triangle list and the remainder as a line list — two draw
/// calls, one upload, one buffer. Two buffers would be two maps a frame for no gain.</para>
///
/// <para><b>The pass this feeds is OPAQUE</b>, so nothing here needs sorting and back faces need no
/// culling: a depth buffer orders fragments exactly. Per-vertex alpha still means something, because
/// it lands in the offscreen surface's alpha channel and survives to the blit — which is how the
/// gridlines come out fainter than the buildings without any blending in the pass itself.</para>
/// </summary>
internal sealed class CityMeshBuilder
{
    private readonly List<CityVertex> _tris  = new(32_768);
    private readonly List<CityVertex> _lines = new(16_384);

    /// <summary>Pushes window and door quads this far out of their wall.
    ///
    /// <para>Without it they are exactly coplanar with the wall, and a depth test on coplanar
    /// geometry is a coin flip per pixel — the classic z-fighting speckle. Small enough not to read
    /// as a raised panel at any distance the city is drawn at.</para>
    /// </summary>
    private const float FacadeOffset = 0.03f;

    /// <summary>Alpha for gridlines and the checkerboard, written into the surface's alpha channel
    /// so the grid reads as lighter than the buildings after the blit.</summary>
    private const float GridLineAlpha  = 0.55f;
    private const float GridFillAlpha  = 0.16f;
    private const float MajorLineAlpha = 0.34f;

    public int TriangleVertexCount => _tris.Count;
    public int TotalVertexCount    => _tris.Count + _lines.Count;

    public void Clear()
    {
        _tris.Clear();
        _lines.Clear();
    }

    /// <summary>Triangles then lines, contiguous, ready to upload.</summary>
    public CityVertex[] ToArray()
    {
        var all = new CityVertex[_tris.Count + _lines.Count];
        _tris.CopyTo(all, 0);
        _lines.CopyTo(all, _tris.Count);
        return all;
    }

    // ── buildings ────────────────────────────────────────────────────────────

    public void AddBuilding(TestCityService city, CityBuilding b, in CityLook look)
    {
        float topY = b.BaseY + b.Height;

        for (int s = 0; s < 4; s++)
        {
            var side = (CitySide)s;
            var (a, c, n) = TestCityFacade.Wall(city, b, side);

            var tint = b.Rgb * TestCityFacade.Shade(n);

            // Skirt to top. The skirt reaches down to the lowest ground under the footprint so a
            // building on a slope shows no daylight under its downhill corner; windows and the door
            // are laid out from the HIGHEST, which is the visible ground level.
            Quad(_tris,
                 new Vector3(a.X, b.SkirtY, a.Y), new Vector3(c.X, b.SkirtY, c.Y),
                 new Vector3(c.X, topY,     c.Y), new Vector3(a.X, topY,     a.Y),
                 new Vector4(tint, 1f));

            if (look.ShowWindows) AddWindows(b, a, c, n, topY, look);
            if (b.DoorSide == side) AddDoor(b, a, c, n, topY, look);
        }

        if (!look.ShowRoofs) return;

        var (r0, r1, r2, r3) = TestCityFacade.Footprint(city, b);
        var roof = b.Rgb * TestCityFacade.Shade(Vector3.UnitY);

        Quad(_tris,
             new Vector3(r0.X, topY, r0.Y), new Vector3(r1.X, topY, r1.Y),
             new Vector3(r2.X, topY, r2.Y), new Vector3(r3.X, topY, r3.Y),
             new Vector4(roof, 1f));
    }

    private void AddWindows(CityBuilding b, Vector2 a, Vector2 c, Vector3 normal, float topY,
                            in CityLook look)
    {
        float span = topY - b.BaseY;
        if (span <= 0.5f) return;

        int   floors  = Math.Clamp(b.Floors, 1, TestCityFacade.MaxWindowRows);
        float wallLen = Vector2.Distance(a, c);
        int   cols    = TestCityFacade.Columns(wallLen);

        float cell  = 1f / cols;
        float halfU = cell * TestCityFacade.WindowWidthFrac * 0.5f;

        var push = normal * FacadeOffset;

        for (int f = 0; f < floors; f++)
        {
            // The door owns the ground floor of its own wall. Threading windows around it would put
            // a half-window beside a door on a narrow facade, which looks like a layout bug.
            if (f == 0 && b.DoorSide == SideOf(normal)) continue;

            float v0 = (f + TestCityFacade.WindowBotFrac) / floors;
            float v1 = (f + TestCityFacade.WindowTopFrac) / floors;

            for (int k = 0; k < cols; k++)
            {
                float mid = (k + 0.5f) * cell;

                bool lit = TestCityFacade.Lit(b.Seed, f, k, look.LitPercent);
                var  col = new Vector4(lit ? look.LitRgb : look.DarkRgb, 1f);

                Quad(_tris,
                     Facade(a, c, b.BaseY, span, mid - halfU, v0) + push,
                     Facade(a, c, b.BaseY, span, mid + halfU, v0) + push,
                     Facade(a, c, b.BaseY, span, mid + halfU, v1) + push,
                     Facade(a, c, b.BaseY, span, mid - halfU, v1) + push,
                     col);
            }
        }
    }

    private void AddDoor(CityBuilding b, Vector2 a, Vector2 c, Vector3 normal, float topY,
                         in CityLook look)
    {
        float span = topY - b.BaseY;
        if (span <= 0.5f) return;

        float wallLen = Vector2.Distance(a, c);
        if (wallLen <= 0.2f) return;

        // Clamped to the wall rather than assumed to fit: a one-tile building is narrower than the
        // door, and a door wider than its wall would wrap round the corner.
        float halfU = MathF.Min(TestCityFacade.DoorWidth, wallLen * 0.5f) / wallLen * 0.5f;
        float v1    = MathF.Min(TestCityFacade.DoorHeight, span * 0.8f) / span;

        var push = normal * FacadeOffset;
        var col  = new Vector4(look.DarkRgb, 1f);

        Quad(_tris,
             Facade(a, c, b.BaseY, span, 0.5f - halfU, 0f) + push,
             Facade(a, c, b.BaseY, span, 0.5f + halfU, 0f) + push,
             Facade(a, c, b.BaseY, span, 0.5f + halfU, v1) + push,
             Facade(a, c, b.BaseY, span, 0.5f - halfU, v1) + push,
             col);

        // A brighter frame, as two lines up the sides and one across the lintel. Lines rather than a
        // second quad: a frame quad behind the door would need a third offset layer to avoid
        // fighting with the door itself.
        var frame = new Vector4(look.LitRgb * 0.85f, 0.9f);

        var d0 = Facade(a, c, b.BaseY, span, 0.5f - halfU, 0f) + push;
        var d1 = Facade(a, c, b.BaseY, span, 0.5f + halfU, 0f) + push;
        var d2 = Facade(a, c, b.BaseY, span, 0.5f + halfU, v1) + push;
        var d3 = Facade(a, c, b.BaseY, span, 0.5f - halfU, v1) + push;

        Line(d0, d3, frame);
        Line(d1, d2, frame);
        Line(d3, d2, frame);
    }

    /// <summary>A point on a facade: <paramref name="u"/> across, <paramref name="v"/> up from the
    /// building's visible ground level to its top.</summary>
    private static Vector3 Facade(Vector2 a, Vector2 c, float baseY, float span, float u, float v)
    {
        var flat = Vector2.Lerp(a, c, u);
        return new Vector3(flat.X, baseY + span * v, flat.Y);
    }

    /// <summary>Which side an outward normal belongs to. Cheaper than threading the side through,
    /// and exact — the four normals are axis-aligned unit vectors by construction.</summary>
    private static CitySide SideOf(Vector3 n) =>
        n.X > 0.5f ? CitySide.PosX
      : n.X < -0.5f ? CitySide.NegX
      : n.Z > 0.5f ? CitySide.PosZ
      : CitySide.NegZ;

    // ── the grid ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The tile grid as GPU geometry, through the same emitter the painted path uses — so the two
    /// cannot disagree about which lines exist or where the fine window sits.
    ///
    /// <para>Getting the grid into this pass rather than leaving it on the ImGui list is not
    /// cosmetic: ImGui draws after this surface is blitted, so a painted grid would lie on TOP of
    /// every building. Here it is depth-tested against them and against the world.</para>
    /// </summary>
    public void AddGrid(TestCityService city, Vector3 centre, Vector3 rgb, int fineTiles,
                        int majorEvery, bool checker)
    {
        TestCityGrid.Emit(city, centre, fineTiles, majorEvery, checker,
            (p, q, major) => Line(p, q, new Vector4(rgb, major ? MajorLineAlpha : GridLineAlpha)),
            (p, q, r, s)  => Quad(_tris, p, q, r, s, new Vector4(rgb, GridFillAlpha)));
    }

    // ── primitives ───────────────────────────────────────────────────────────

    /// <summary>Two triangles over a planar quad. Wound consistently, though nothing depends on it:
    /// the rasterizer runs with culling off precisely so there is no winding rule to break.</summary>
    private static void Quad(List<CityVertex> into, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                             Vector4 colour)
    {
        into.Add(new CityVertex(p0, colour));
        into.Add(new CityVertex(p1, colour));
        into.Add(new CityVertex(p2, colour));

        into.Add(new CityVertex(p0, colour));
        into.Add(new CityVertex(p2, colour));
        into.Add(new CityVertex(p3, colour));
    }

    private void Line(Vector3 a, Vector3 b, Vector4 colour)
    {
        _lines.Add(new CityVertex(a, colour));
        _lines.Add(new CityVertex(b, colour));
    }
}
#endif
