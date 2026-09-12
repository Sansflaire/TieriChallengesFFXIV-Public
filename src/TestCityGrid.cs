#if DEV_BUILD
using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. Draws <see cref="TestCityService"/>'s tile grid on the ground.
///
/// <para><b>Why this exists instead of reusing <see cref="DigVolumeRender.DrawGroundGrid"/>, which
/// it did until 0.84.54.8.</b> That drawer checkers every cell and runs a polyline through every
/// vertex of the lattice it is given, which is exactly right for a dig site of 40 cells and
/// impossible at the extents Sansflaire asked for: a 400-tile grid is 160,000 checker quads and 800
/// polylines a frame. The reuse was not wrong, it simply does not survive a three-order-of-magnitude
/// change in tile count.</para>
///
/// <para><b>So the grid is drawn in two parts, and neither scales with the city.</b> A FINE window
/// of at most <see cref="MaxFine"/> tiles that follows the player — which is where you actually
/// inspect whether a building lines up — plus MAJOR lines every N tiles across the whole extent, so
/// the far city still reads as gridded. Both are capped, so no slider combination can make this
/// expensive.</para>
///
/// <para><b>Every height comes from <see cref="TestCityService.HeightAt"/> and every corner from
/// <see cref="TestCityService.TileCorner"/></b> — the same two functions the buildings use. That is
/// what keeps "the building sits on that tile" true now that tiles are no longer lattice points.</para>
///
/// <para>Painted on <c>GetBackgroundDrawList</c>: over the game, under every ImGui window, consuming
/// no input. Not depth-tested, so the grid reads through a rise rather than being swallowed by it —
/// which is what makes the city legible from outside as well as inside.</para>
/// </summary>
internal static class TestCityGrid
{
    /// <summary>Ceiling on the fine window, in tiles. 64 is ~8,500 projections a frame for the lines
    /// alone, which is already the most expensive thing this feature does.</summary>
    public const int MaxFine = 64;

    /// <summary>Above this many fine tiles the checkerboard is dropped and only lines are drawn. The
    /// checker is a filled quad per tile and it is what gets expensive first.</summary>
    private const int MaxCheckerTiles = 40;

    /// <summary>Cap on major lines per axis, and the world spacing their polylines are sampled at.</summary>
    private const int   MaxMajorLines   = 64;
    private const float MajorSampleStep = 1.5f;
    private const int   MaxMajorSamples = 96;

    /// <summary>Lifts the grid clear of the surface so it does not vanish into a dip in the mesh.
    /// Nothing here is depth-tested, so this is not about z-fighting.</summary>
    private const float Lift = 0.06f;

    public static void Draw(TestCityService city, Vector3 eye, Vector3 rgb, int fineTiles,
                            int majorEvery, bool checker)
    {
        if (!city.IsBuilt) return;

        try
        {
            var list = ImGui.GetBackgroundDrawList();

            // The fine window follows the PLAYER, not the camera. It marks out the ground you are
            // standing on, which is what was asked for and what stays useful when the camera swings
            // away to look at a skyline.
            var lp = Plugin.ObjectTable.LocalPlayer;
            var centre = lp?.Position ?? eye;

            DrawMajor(list, city, rgb, majorEvery);
            DrawFine(list, city, centre, rgb, fineTiles, checker);
        }
        catch (Exception ex)
        {
            Diag.Error($"[City] grid draw failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The fine 1:1 grid, in a window around the player: a line on every tile boundary, and
    /// optionally a checker so individual tiles are countable.
    /// </summary>
    private static void DrawFine(ImDrawListPtr list, TestCityService city, Vector3 centre,
                                 Vector3 rgb, int fineTiles, bool checker)
    {
        int span = Math.Clamp(fineTiles, 2, MaxFine);
        int tiles = city.TilesAcross;

        var (pi, pj) = city.TileIndexAt(centre.X, centre.Z);

        int i0 = Math.Clamp(pi - span / 2, 0, Math.Max(0, tiles - 1));
        int j0 = Math.Clamp(pj - span / 2, 0, Math.Max(0, tiles - 1));
        int i1 = Math.Min(tiles, i0 + span);
        int j1 = Math.Min(tiles, j0 + span);

        if (i1 <= i0 || j1 <= j0) return;

        uint line = Color(rgb, 0.55f);
        uint fill = Color(rgb, 0.16f);

        // Checker first so the lines land on top of it.
        if (checker && (i1 - i0) <= MaxCheckerTiles && (j1 - j0) <= MaxCheckerTiles)
        {
            for (int i = i0; i < i1; i++)
            {
                for (int j = j0; j < j1; j++)
                {
                    if (((i + j) & 1) != 0) continue;

                    if (!Project(city, i,     j,     out var s0)) continue;
                    if (!Project(city, i + 1, j,     out var s1)) continue;
                    if (!Project(city, i + 1, j + 1, out var s2)) continue;
                    if (!Project(city, i,     j + 1, out var s3)) continue;

                    list.AddQuadFilled(s0, s1, s2, s3, fill);
                }
            }
        }

        // Boundaries run to i1/j1 inclusive, or the window's far edge has no line on it.
        for (int i = i0; i <= i1; i++) Polyline(list, city, i, j0, j1, true,  line);
        for (int j = j0; j <= j1; j++) Polyline(list, city, j, i0, i1, false, line);
    }

    /// <summary>
    /// Lines every <paramref name="every"/> tiles across the WHOLE city, so the extent is legible
    /// from a distance without drawing a quarter-million fine tiles.
    ///
    /// <para>Sampled at a fixed world spacing rather than per tile: at a 0.05-yalm tile size a line
    /// across a 400-tile city is 20 yalms and 401 points, and at 8 yalms it is 3,200 yalms and the
    /// same 401 points. Spacing in yalms makes the cost depend on the length being drawn, which is
    /// the thing that actually matters.</para>
    /// </summary>
    private static void DrawMajor(ImDrawListPtr list, TestCityService city, Vector3 rgb, int every)
    {
        int tiles = city.TilesAcross;
        int step  = Math.Max(1, every);

        // Widen the step until the count fits. Honest degradation: the grid gets coarser rather than
        // partially drawn, and a half-drawn grid is worse than a coarse one because it reads as the
        // city having an edge where it does not.
        while (tiles / step > MaxMajorLines) step *= 2;

        uint col = Color(rgb, 0.34f);

        int samples = Math.Clamp((int)MathF.Ceiling(city.Extent / MajorSampleStep) + 1, 2,
                                 MaxMajorSamples);

        for (int i = 0; i <= tiles; i += step)
        {
            SampledLine(list, city, i, true,  samples, col);
            SampledLine(list, city, i, false, samples, col);
        }
    }

    /// <summary>One major line, drawn across the city at a fixed number of samples.</summary>
    private static void SampledLine(ImDrawListPtr list, TestCityService city, int index, bool alongZ,
                                    int samples, uint col)
    {
        Vector2? prev = null;

        for (int k = 0; k < samples; k++)
        {
            float t = k / (float)(samples - 1) * city.TilesAcross;

            // One corner is enough for either orientation: TileCorner(index, 0) pins X and starts at
            // the grid's first Z, TileCorner(0, index) pins Z and starts at its first X.
            var corner = alongZ ? city.TileCorner(index, 0) : city.TileCorner(0, index);

            float x = alongZ ? corner.X : corner.X + t * city.BuiltTileSize;
            float z = alongZ ? corner.Y + t * city.BuiltTileSize : corner.Y;

            var p = new Vector3(x, city.HeightAt(x, z) + Lift, z);

            // Broken on a projection failure rather than joined across the gap — the house rule for
            // every polyline drawn into the world, because a point behind the camera has no screen
            // position and joining to it whips a line across the viewport.
            if (!AreaOverlay.Project(p, out var s)) { prev = null; continue; }

            if (prev.HasValue) list.AddLine(prev.Value, s, col, 1.6f);
            prev = s;
        }
    }

    /// <summary>One fine gridline, from tile boundary to tile boundary so it bends with the ground.</summary>
    private static void Polyline(ImDrawListPtr list, TestCityService city, int index, int from, int to,
                                 bool alongZ, uint col)
    {
        Vector2? prev = null;

        for (int k = from; k <= to; k++)
        {
            bool ok = alongZ ? Project(city, index, k, out var s) : Project(city, k, index, out s);

            if (!ok) { prev = null; continue; }

            if (prev.HasValue) list.AddLine(prev.Value, s, col, 1.3f);
            prev = s;
        }
    }

    private static bool Project(TestCityService city, int i, int j, out Vector2 screen)
    {
        var g = city.TileCornerGround(i, j);

        // Via AreaOverlay.Project, which wraps the THREE-argument WorldToScreen and fails only for
        // genuinely behind-the-camera points. The two-argument overload also returns false for
        // anything outside the viewport, which silently discards geometry the player is standing next
        // to — see Issues/013.
        return AreaOverlay.Project(new Vector3(g.X, g.Y + Lift, g.Z), out screen);
    }

    private static uint Color(Vector3 rgb, float alpha) =>
        ImGui.GetColorU32(new Vector4(rgb.X, rgb.Y, rgb.Z, alpha));
}
#endif
