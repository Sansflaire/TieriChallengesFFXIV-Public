#if DEV_BUILD
using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. <b>Decides which gridlines exist, and leaves the drawing to whoever asked.</b>
///
/// <para><see cref="Emit"/> is the whole of the grid's logic — the fine window that follows the
/// player, the caps, the major step that widens itself — and it hands each line and each checker
/// cell to a callback. Two consumers use it: <see cref="Draw"/> paints straight onto the ImGui
/// background list, and <see cref="CityMeshBuilder.AddGrid"/> turns the same calls into GPU
/// geometry. <b>One copy of the rules, two renderers</b> — the alternative was the grid and the
/// buildings disagreeing about where a tile edge is, which is the one thing this feature exists to
/// look at.</para>
///
/// <para><b>Why this replaced reusing <see cref="DigVolumeRender.DrawGroundGrid"/> at 0.84.54.8.</b>
/// That drawer checkers every cell and runs a polyline through every lattice vertex, which is right
/// for a dig site of 40 cells and impossible at 400 tiles — 160,000 checker quads a frame. The reuse
/// was not wrong; it does not survive a three-order-of-magnitude change in tile count.</para>
///
/// <para><b>Every height comes from <see cref="TestCityService.HeightAt"/> and every corner from
/// <see cref="TestCityService.TileCorner"/></b> — the same two functions the buildings use. That is
/// what keeps "the building sits on that tile" true now that tiles are not lattice points.</para>
/// </summary>
internal static class TestCityGrid
{
    /// <summary>Ceiling on the fine window, in tiles.</summary>
    public const int MaxFine = 64;

    /// <summary>Above this many fine tiles the checkerboard is dropped and only lines are drawn — a
    /// filled quad per tile is what gets expensive first.</summary>
    private const int MaxCheckerTiles = 40;

    /// <summary>Cap on major lines per axis, and the world spacing their polylines are sampled at.</summary>
    private const int   MaxMajorLines   = 64;
    private const float MajorSampleStep = 1.5f;
    private const int   MaxMajorSamples = 96;

    /// <summary>Lifts the grid clear of the surface so it does not vanish into a dip. Nothing here is
    /// depth-tested against the terrain by the painted path, so this is not about z-fighting there —
    /// but it is exactly that on the D3D path, where the ground is real geometry in the depth buffer.</summary>
    private const float Lift = 0.06f;

    /// <summary>A line segment. <c>major</c> distinguishes the sparse whole-city lines from the fine
    /// ones so a consumer can colour them differently without re-deriving which is which.</summary>
    public delegate void LineSink(Vector3 a, Vector3 b, bool major);

    /// <summary>One checkerboard cell, as a ground-following quad.</summary>
    public delegate void QuadSink(Vector3 a, Vector3 b, Vector3 c, Vector3 d);

    /// <summary>
    /// Walks the grid and reports it. <b>No drawing happens here</b> — this is the single source of
    /// which lines and cells make up the grid.
    /// </summary>
    public static void Emit(TestCityService city, Vector3 centre, int fineTiles, int majorEvery,
                            bool checker, LineSink line, QuadSink quad)
    {
        if (!city.IsBuilt) return;

        EmitMajor(city, majorEvery, line);
        EmitFine(city, centre, fineTiles, checker, line, quad);
    }

    /// <summary>Paints the grid onto the ImGui background draw list — the fallback path, used when
    /// the D3D renderer is unavailable or turned off.</summary>
    public static void Draw(TestCityService city, Vector3 eye, Vector3 rgb, int fineTiles,
                            int majorEvery, bool checker)
    {
        if (!city.IsBuilt) return;

        try
        {
            var list = ImGui.GetBackgroundDrawList();

            uint fine  = Colour(rgb, 0.55f);
            uint major = Colour(rgb, 0.34f);
            uint fill  = Colour(rgb, 0.16f);

            // The fine window follows the PLAYER, not the camera. It marks out the ground you are
            // standing on, which is what stays useful when the camera swings away to a skyline.
            var lp     = Plugin.ObjectTable.LocalPlayer;
            var centre = lp?.Position ?? eye;

            Emit(city, centre, fineTiles, majorEvery, checker,
                (a, b, isMajor) =>
                {
                    // Each segment projected independently and dropped if either end fails, rather
                    // than joined across the gap: a point behind the camera has no screen position,
                    // and joining to it whips a line across the viewport.
                    if (!AreaOverlay.Project(a, out var sa)) return;
                    if (!AreaOverlay.Project(b, out var sb)) return;

                    list.AddLine(sa, sb, isMajor ? major : fine, isMajor ? 1.6f : 1.3f);
                },
                (a, b, c, d) =>
                {
                    if (!AreaOverlay.Project(a, out var s0)) return;
                    if (!AreaOverlay.Project(b, out var s1)) return;
                    if (!AreaOverlay.Project(c, out var s2)) return;
                    if (!AreaOverlay.Project(d, out var s3)) return;

                    list.AddQuadFilled(s0, s1, s2, s3, fill);
                });
        }
        catch (Exception ex)
        {
            Diag.Error($"[City] grid draw failed: {ex.Message}");
        }
    }

    /// <summary>The fine 1:1 grid in a window around the player: a line on every tile boundary, and
    /// optionally a checker so individual tiles are countable.</summary>
    private static void EmitFine(TestCityService city, Vector3 centre, int fineTiles, bool checker,
                                 LineSink line, QuadSink quad)
    {
        int span  = Math.Clamp(fineTiles, 2, MaxFine);
        int tiles = city.TilesAcross;

        var (pi, pj) = city.TileIndexAt(centre.X, centre.Z);

        int i0 = Math.Clamp(pi - span / 2, 0, Math.Max(0, tiles - 1));
        int j0 = Math.Clamp(pj - span / 2, 0, Math.Max(0, tiles - 1));
        int i1 = Math.Min(tiles, i0 + span);
        int j1 = Math.Min(tiles, j0 + span);

        if (i1 <= i0 || j1 <= j0) return;

        if (checker && (i1 - i0) <= MaxCheckerTiles && (j1 - j0) <= MaxCheckerTiles)
        {
            for (int i = i0; i < i1; i++)
            {
                for (int j = j0; j < j1; j++)
                {
                    if (((i + j) & 1) != 0) continue;

                    quad(Ground(city, i, j),         Ground(city, i + 1, j),
                         Ground(city, i + 1, j + 1), Ground(city, i,     j + 1));
                }
            }
        }

        // Boundaries run to i1/j1 inclusive, or the window's far edge has no line on it.
        for (int i = i0; i <= i1; i++)
            for (int j = j0; j < j1; j++)
                line(Ground(city, i, j), Ground(city, i, j + 1), false);

        for (int j = j0; j <= j1; j++)
            for (int i = i0; i < i1; i++)
                line(Ground(city, i, j), Ground(city, i + 1, j), false);
    }

    /// <summary>
    /// Lines every N tiles across the WHOLE city, so the extent is legible from a distance without
    /// drawing a quarter of a million fine tiles.
    ///
    /// <para>Sampled at a fixed world spacing rather than per tile: at a 0.05-yalm tile size a line
    /// across a 400-tile city is 20 yalms and 401 points, and at 8 yalms it is 3,200 yalms and the
    /// same 401 points. Spacing in yalms makes the cost depend on the length being drawn, which is
    /// the thing that actually matters.</para>
    /// </summary>
    private static void EmitMajor(TestCityService city, int majorEvery, LineSink line)
    {
        int tiles = city.TilesAcross;
        int step  = Math.Max(1, majorEvery);

        // Widened until the count fits. Honest degradation: the grid gets coarser rather than
        // partially drawn, and a half-drawn grid is worse than a coarse one because it reads as the
        // city having an edge where it does not.
        while (tiles / step > MaxMajorLines) step *= 2;

        int samples = Math.Clamp((int)MathF.Ceiling(city.Extent / MajorSampleStep) + 1, 2,
                                 MaxMajorSamples);

        for (int index = 0; index <= tiles; index += step)
        {
            SampledLine(city, index, true,  samples, line);
            SampledLine(city, index, false, samples, line);
        }
    }

    private static void SampledLine(TestCityService city, int index, bool alongZ, int samples,
                                    LineSink line)
    {
        // One corner is enough for either orientation: TileCorner(index, 0) pins X and starts at the
        // grid's first Z, TileCorner(0, index) pins Z and starts at its first X.
        var corner = alongZ ? city.TileCorner(index, 0) : city.TileCorner(0, index);

        Vector3? prev = null;

        for (int k = 0; k < samples; k++)
        {
            float t = k / (float)(samples - 1) * city.TilesAcross * city.BuiltTileSize;

            float x = alongZ ? corner.X : corner.X + t;
            float z = alongZ ? corner.Y + t : corner.Y;

            var p = new Vector3(x, city.HeightAt(x, z) + Lift, z);

            if (prev.HasValue) line(prev.Value, p, true);
            prev = p;
        }
    }

    private static Vector3 Ground(TestCityService city, int i, int j)
    {
        var g = city.TileCornerGround(i, j);
        return new Vector3(g.X, g.Y + Lift, g.Z);
    }

    private static uint Colour(Vector3 rgb, float alpha) =>
        ImGui.GetColorU32(new Vector4(rgb.X, rgb.Y, rgb.Z, alpha));
}
#endif
