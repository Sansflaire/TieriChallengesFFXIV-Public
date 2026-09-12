#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. Paints <see cref="TestCityService"/>'s buildings as solid cuboids with
/// windows and a door, on <c>GetBackgroundDrawList</c> — over the game, under every ImGui window,
/// consuming no input.
///
/// <para><b>There is no depth buffer here, so three things this file does are not embellishment —
/// they are the only reason a box reads as a box:</b></para>
/// <list type="number">
/// <item><b>Back-face culling by a real 3D normal against the camera position.</b> Not by screen
/// winding: a signed-area test needs a handedness convention to be right about, and getting it
/// backwards draws exactly the four hidden faces — a failure that looks like the city being absent
/// rather than like a sign error. <c>dot(outward normal, eye − face centre) &gt; 0</c> needs no
/// convention and cannot be inverted by accident.</item>
/// <item><b>Painter's ordering, far to near, across every panel of every building.</b> Sorting
/// per-building would be enough only if buildings never overlapped on screen, and in a street grid
/// they overlap constantly. Buildings are disjoint convex boxes, so a global far-to-near pass over
/// front faces is exact for them.</item>
/// <item><b>Flat directional shading per face.</b> Four walls filled in one colour is a silhouette,
/// not a solid. One fixed light, and the form appears for free.</item>
/// </list>
///
/// <para><b>Subdivision happens only on a projection failure.</b> A quad with a corner behind the
/// camera has no meaningful screen position and must be dropped whole — which, standing in a street,
/// means the enormous near wall vanishes. So the full quad is tried first (no seams, one primitive)
/// and only a quad that fails falls back to a subdivided grid, where just the straddling sliver is
/// lost. Subdividing unconditionally would put a faint AA seam on every wall in the city to fix a
/// case that arises on two of them.</para>
/// </summary>
internal static class TestCityRender
{
    /// <summary>Roughly one window column per this many yalms of wall.</summary>
    private const float WindowPitch = 2.4f;

    /// <summary>Fraction of its cell a window fills across, and where in its floor it sits.</summary>
    private const float WindowWidthFrac = 0.52f;
    private const float WindowTopFrac   = 0.78f;
    private const float WindowBotFrac   = 0.32f;

    private const int MaxWindowCols = 10;
    private const int MaxWindowRows = 40;

    private const float DoorWidth  = 1.4f;
    private const float DoorHeight = 2.4f;

    /// <summary>Subdivision used ONLY by the fallback path — see the class remarks.</summary>
    private const int FallbackCols = 3;
    private const int FallbackRows = 6;

    /// <summary>
    /// A fixed light. Direction rather than a real sun: the point is that the four walls and the roof
    /// come out at five different brightnesses, and a light that tracked Eorzean time would make a
    /// building's north face change identity while you walked round it.
    /// </summary>
    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.42f, 0.82f, -0.39f));

    /// <summary>One wall or one roof, resolved to world points and ready to sort.</summary>
    private struct Panel
    {
        public CityBuilding Owner;
        public bool         IsRoof;
        public bool         HasDoor;

        /// <summary>The filled quad, wound consistently around the face.</summary>
        public Vector3 P0, P1, P2, P3;

        /// <summary>Wall only — the two base ends at the building's ground level.</summary>
        public Vector3 A, B;

        public float   TopY;
        public Vector3 Rgb;

        /// <summary>Squared distance from the eye to the face centre. Squared is enough: it is only
        /// ever compared, and the square root would be one per panel per frame for nothing.</summary>
        public float Depth;
    }

    /// <summary>
    /// Draws every building. Returns the number of panels actually submitted, which is what lets the
    /// lab tell "culled backwards" apart from "nothing was placed".
    /// </summary>
    public static int Draw(Vector3[,] grid, IReadOnlyList<CityBuilding> buildings, Vector3 eye,
                           in CityLook look)
    {
        int n = grid.GetLength(0);
        if (n < 2 || buildings.Count == 0) return 0;

        var panels = new List<Panel>(buildings.Count * 5);

        foreach (var b in buildings) Collect(panels, grid, n, b, eye, look);

        // Far to near. A stable comparison on the squared distance; ties keep their collection order,
        // which for two coplanar faces of different buildings is arbitrary either way.
        panels.Sort(static (x, y) => y.Depth.CompareTo(x.Depth));

        var list = ImGui.GetBackgroundDrawList();

        foreach (var p in panels) Paint(list, p, look);

        return panels.Count;
    }

    /// <summary>
    /// Turns one building into its visible panels. Four walls and a roof, each kept only if it faces
    /// the eye.
    /// </summary>
    private static void Collect(List<Panel> into, Vector3[,] grid, int n, CityBuilding b, Vector3 eye,
                                in CityLook look)
    {
        int i0 = Math.Clamp(b.TileX, 0, n - 1);
        int j0 = Math.Clamp(b.TileZ, 0, n - 1);
        int i1 = Math.Clamp(b.TileX + b.TilesX, 0, n - 1);
        int j1 = Math.Clamp(b.TileZ + b.TilesZ, 0, n - 1);

        if (i1 <= i0 || j1 <= j0) return;

        float topY = b.BaseY + b.Height;

        // XZ read straight out of the lattice the floor grid is drawn from. This is the whole of
        // "buildings line up with the tiles" — there is no second calculation to disagree with it.
        Vector2 c00 = Flat(grid[i0, j0]);
        Vector2 c10 = Flat(grid[i1, j0]);
        Vector2 c11 = Flat(grid[i1, j1]);
        Vector2 c01 = Flat(grid[i0, j1]);

        // The base ring, walked consistently, paired with each edge's outward normal. Normals are
        // written down rather than derived: the footprint is axis-aligned by construction, so there
        // is nothing to compute and therefore nothing to get the sign of wrong.
        AddWall(into, b, c00, c10, new Vector3(0f, 0f, -1f), CitySide.NegZ, topY, eye, look);
        AddWall(into, b, c10, c11, new Vector3(1f, 0f,  0f), CitySide.PosX, topY, eye, look);
        AddWall(into, b, c11, c01, new Vector3(0f, 0f,  1f), CitySide.PosZ, topY, eye, look);
        AddWall(into, b, c01, c00, new Vector3(-1f, 0f, 0f), CitySide.NegX, topY, eye, look);

        if (!look.ShowRoofs || eye.Y <= topY) return;

        var r0 = new Vector3(c00.X, topY, c00.Y);
        var r1 = new Vector3(c10.X, topY, c10.Y);
        var r2 = new Vector3(c11.X, topY, c11.Y);
        var r3 = new Vector3(c01.X, topY, c01.Y);

        var centre = (r0 + r1 + r2 + r3) * 0.25f;

        into.Add(new Panel
        {
            Owner  = b,
            IsRoof = true,
            P0     = r0, P1 = r1, P2 = r2, P3 = r3,
            TopY   = topY,
            Rgb    = b.Rgb * Shade(Vector3.UnitY),
            Depth  = (eye - centre).LengthSquared(),
        });
    }

    private static void AddWall(List<Panel> into, CityBuilding b, Vector2 a, Vector2 c,
                                Vector3 normal, CitySide side, float topY, Vector3 eye,
                                in CityLook look)
    {
        // Walls run from the LOWEST ground under the footprint up to the top, while the windows and
        // the door are laid out from the highest. The skirt is what stops a downhill corner showing
        // daylight under the building on sloped ground; it is not part of the facade.
        var p0 = new Vector3(a.X, b.SkirtY, a.Y);
        var p1 = new Vector3(c.X, b.SkirtY, c.Y);
        var p2 = new Vector3(c.X, topY,     c.Y);
        var p3 = new Vector3(a.X, topY,     a.Y);

        var centre = (p0 + p1 + p2 + p3) * 0.25f;

        // The one visibility test. Unambiguous, and immune to the sign error a screen-winding test
        // invites — see the class remarks.
        if (Vector3.Dot(normal, eye - centre) <= 0f) return;

        into.Add(new Panel
        {
            Owner   = b,
            IsRoof  = false,
            HasDoor = b.DoorSide == side,
            P0      = p0, P1 = p1, P2 = p2, P3 = p3,
            A       = new Vector3(a.X, b.BaseY, a.Y),
            B       = new Vector3(c.X, b.BaseY, c.Y),
            TopY    = topY,
            Rgb     = b.Rgb * Shade(normal),
            Depth   = (eye - centre).LengthSquared(),
        });
    }

    /// <summary>Face, then its windows and door, then its outline — in that order, so the detail
    /// lands on top of the fill rather than under it.</summary>
    private static void Paint(ImDrawListPtr list, in Panel p, in CityLook look)
    {
        FillQuad(list, p.P0, p.P1, p.P2, p.P3, Col(p.Rgb, look.WallAlpha));

        if (!p.IsRoof && look.ShowWindows) Windows(list, p, look);
        if (!p.IsRoof && p.HasDoor)        Door(list, p, look);

        if (look.ShowEdges)
        {
            // Darkened rather than lightened: an outline brighter than the fill reads as a wireframe
            // over the building, and what is wanted is the crease where two faces meet.
            uint edge = Col(p.Rgb * 0.45f, MathF.Min(1f, look.WallAlpha + 0.10f));
            Outline(list, p.P0, p.P1, p.P2, p.P3, edge);
        }
    }

    /// <summary>
    /// A row of windows per floor. Laid out in the wall's own <c>(u, v)</c> frame, so the same code
    /// works whichever way the wall faces and nothing has to know about world axes.
    /// </summary>
    private static void Windows(ImDrawListPtr list, in Panel p, in CityLook look)
    {
        float span = p.TopY - p.A.Y;
        if (span <= 0.5f) return;

        int floors = Math.Clamp(p.Owner.Floors, 1, MaxWindowRows);

        float wallLen = Vector2.Distance(Flat(p.A), Flat(p.B));
        int   cols    = Math.Clamp((int)MathF.Floor(wallLen / WindowPitch), 1, MaxWindowCols);

        float cell  = 1f / cols;
        float halfU = cell * WindowWidthFrac * 0.5f;

        float alpha = MathF.Min(1f, look.WallAlpha + 0.10f);

        for (int f = 0; f < floors; f++)
        {
            // The door owns the ground floor of its own wall. Threading windows around it would put
            // a half-window beside a door on a narrow facade, which looks like a layout bug.
            if (f == 0 && p.HasDoor) continue;

            float v0 = (f + WindowBotFrac) / floors;
            float v1 = (f + WindowTopFrac) / floors;

            for (int c = 0; c < cols; c++)
            {
                float mid = (c + 0.5f) * cell;

                bool lit = Lit(p.Owner.Seed, f, c, look.LitPercent);
                uint col = Col(lit ? look.LitRgb : look.DarkRgb, alpha);

                FillQuad(list,
                         OnWall(p, mid - halfU, v0),
                         OnWall(p, mid + halfU, v0),
                         OnWall(p, mid + halfU, v1),
                         OnWall(p, mid - halfU, v1),
                         col);
            }
        }
    }

    /// <summary>A door at the base, centred on the wall, with a frame so it reads as an opening
    /// rather than as a dark patch.</summary>
    private static void Door(ImDrawListPtr list, in Panel p, in CityLook look)
    {
        float span = p.TopY - p.A.Y;
        if (span <= 0.5f) return;

        float wallLen = Vector2.Distance(Flat(p.A), Flat(p.B));
        if (wallLen <= 0.2f) return;

        // Clamped to the wall rather than assumed to fit: a one-tile building is narrower than the
        // door, and a door wider than its wall would wrap round the corner.
        float halfU = MathF.Min(DoorWidth, wallLen * 0.5f) / wallLen * 0.5f;
        float v1    = MathF.Min(DoorHeight, span * 0.8f) / span;

        var d0 = OnWall(p, 0.5f - halfU, 0f);
        var d1 = OnWall(p, 0.5f + halfU, 0f);
        var d2 = OnWall(p, 0.5f + halfU, v1);
        var d3 = OnWall(p, 0.5f - halfU, v1);

        float alpha = MathF.Min(1f, look.WallAlpha + 0.10f);

        FillQuad(list, d0, d1, d2, d3, Col(look.DarkRgb, alpha));
        Outline(list, d0, d1, d2, d3, Col(look.LitRgb * 0.85f, alpha), 2f);
    }

    /// <summary>A point on a wall in its own frame: <paramref name="u"/> across, <paramref name="v"/>
    /// up from the building's ground level to its top.</summary>
    private static Vector3 OnWall(in Panel p, float u, float v)
    {
        var flat = Vector3.Lerp(p.A, p.B, u);
        return new Vector3(flat.X, p.A.Y + (p.TopY - p.A.Y) * v, flat.Z);
    }

    /// <summary>
    /// Whether one window is lit. <b>Deterministic per window</b> — a per-frame roll would strobe
    /// the whole city, which reads as a rendering fault. Nothing cryptographic: a multiply-xor mix,
    /// well spread enough that neighbours differ.
    /// </summary>
    private static bool Lit(int seed, int floor, int col, int percent)
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

    /// <summary>Flat directional shading, floored well above zero so a wall facing away from the
    /// light is dim rather than black — five readable brightnesses, not one plus a silhouette.</summary>
    private static float Shade(Vector3 normal) =>
        0.52f + 0.48f * MathF.Max(0f, Vector3.Dot(normal, Sun));

    /// <summary>
    /// Fills a planar quad, <b>falling back to subdivision only when a corner will not project</b>.
    ///
    /// <para>One primitive is both cheaper and cleaner — abutting translucent quads leave a faint
    /// antialiasing seam, so subdividing every wall would trade a rare failure for a permanent
    /// artefact. See the class remarks.</para>
    /// </summary>
    private static void FillQuad(ImDrawListPtr list, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                                 uint col)
    {
        if (AreaOverlay.Project(p0, out var s0) && AreaOverlay.Project(p1, out var s1)
         && AreaOverlay.Project(p2, out var s2) && AreaOverlay.Project(p3, out var s3))
        {
            // Wound consistently around the face. AddQuadFilled goes through AddConvexPolyFilled,
            // which needs a convex ring — a figure-of-eight winding renders as two slivers rather
            // than erroring.
            list.AddQuadFilled(s0, s1, s2, s3, col);
            return;
        }

        Subdivided(list, p0, p1, p2, p3, col);
    }

    /// <summary>
    /// The fallback: a bilinear grid over the quad, dropping only the cells that straddle the camera
    /// plane instead of the whole face.
    /// </summary>
    private static void Subdivided(ImDrawListPtr list, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                                   uint col)
    {
        for (int r = 0; r < FallbackRows; r++)
        {
            float v0 = r / (float)FallbackRows;
            float v1 = (r + 1) / (float)FallbackRows;

            for (int c = 0; c < FallbackCols; c++)
            {
                float u0 = c / (float)FallbackCols;
                float u1 = (c + 1) / (float)FallbackCols;

                if (!AreaOverlay.Project(Bilinear(p0, p1, p2, p3, u0, v0), out var s0)) continue;
                if (!AreaOverlay.Project(Bilinear(p0, p1, p2, p3, u1, v0), out var s1)) continue;
                if (!AreaOverlay.Project(Bilinear(p0, p1, p2, p3, u1, v1), out var s2)) continue;
                if (!AreaOverlay.Project(Bilinear(p0, p1, p2, p3, u0, v1), out var s3)) continue;

                list.AddQuadFilled(s0, s1, s2, s3, col);
            }
        }
    }

    private static Vector3 Bilinear(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u, float v)
        => Vector3.Lerp(Vector3.Lerp(p0, p1, u), Vector3.Lerp(p3, p2, u), v);

    /// <summary>
    /// Strokes a closed quad. <b>Breaks the line on a projection failure rather than joining across
    /// the gap</b> — the house rule for every polyline drawn into the world, because a point behind
    /// the camera has no screen position and joining to it whips a line across the viewport.
    /// </summary>
    private static void Outline(ImDrawListPtr list, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                                uint col, float thickness = 1.4f)
    {
        Span<Vector3> ring = stackalloc Vector3[5] { p0, p1, p2, p3, p0 };

        Vector2? prev = null;

        for (int i = 0; i < ring.Length; i++)
        {
            if (!AreaOverlay.Project(ring[i], out var s)) { prev = null; continue; }
            if (prev.HasValue) list.AddLine(prev.Value, s, col, thickness);
            prev = s;
        }
    }

    private static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    private static uint Col(Vector3 rgb, float alpha) => ImGui.GetColorU32(
        new Vector4(Math.Clamp(rgb.X, 0f, 1f), Math.Clamp(rgb.Y, 0f, 1f),
                    Math.Clamp(rgb.Z, 0f, 1f), Math.Clamp(alpha, 0f, 1f)));
}
#endif
