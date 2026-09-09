#if DEV_BUILD
using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. Draws a box volume as four <b>solid-at-the-base, transparent-at-the-top
/// gradient walls</b>, so a zone reads as a place you are standing in rather than as a wireframe
/// floating in the air.
///
/// <para><b>How the gradient is made.</b> ImGui has no per-vertex gradient for an arbitrary quad —
/// <c>AddRectFilledMultiColor</c> exists but only for a screen-axis-aligned rectangle, and a
/// projected wall is never one of those (perspective slants its edges even though the camera has no
/// roll). So each wall is a grid of quads, each filled at the alpha for its height.</para>
///
/// <para><b>Three things were wrong in the first version and all three are worth remembering,
/// because each was invisible in code review and obvious in the world:</b></para>
/// <list type="number">
/// <item><b>The projection gate discarded visible geometry.</b> It used
/// <c>WorldToScreen(pos, out screen)</c>, which is only true when the point is in front of the
/// camera <i>and inside the viewport</i>. The player stands inside the site, so the near walls are
/// enormous and virtually always have a corner past a screen edge — every one of their quads was
/// skipped, and a wall only appeared when the camera happened to frame the whole of it. Now via
/// <see cref="AreaOverlay.Project"/>, which fails only for genuinely-behind-the-camera points.</item>
/// <item><b>The opaque base was underground.</b> The site box is deliberately tall so its
/// containment test survives sloped ground, and the wall was drawn over the box's full height — so
/// its solid bottom sat metres below the surface and only the faded top half was ever above
/// ground. The wall is now drawn from the site's ground level upward, over its own height, which is
/// unrelated to the volume's.</item>
/// <item><b>One quad per row spanned the whole wall.</b> Even with the gate fixed, a single quad
/// crossing behind the camera has no valid projection and must be dropped whole. Subdividing across
/// the width means only the sliver that actually straddles the camera is lost, and it also cuts the
/// perspective error — screen-space interpolation across a very long quad is affine and visibly
/// wrong.</item>
/// </list>
///
/// <para>The exact per-vertex route (<c>PrimReserve</c>/<c>PrimWriteVtx</c>/<c>PrimWriteIdx</c>) is
/// exposed by the binding and remains the upgrade if banding shows, but it needs
/// <c>_VtxCurrentIdx</c> confirmed first — a wrong index count corrupts the shared draw list, which
/// is a graphical crash rather than a wrong colour.</para>
///
/// <para>Painted on <c>GetBackgroundDrawList</c>, like <see cref="AreaOverlay"/> — over the game,
/// under every ImGui window, consuming no input.</para>
/// </summary>
internal static class DigVolumeRender
{
    /// <summary>Vertical slices per wall. This is what makes the gradient smooth.</summary>
    private const int Rows = 22;

    /// <summary>
    /// Horizontal slices per wall. Not for the gradient — purely so a quad straddling the camera
    /// plane costs one narrow column instead of the entire wall, and to keep perspective honest.
    /// </summary>
    private const int Cols = 10;

    /// <summary>
    /// Alpha at the very bottom of a wall. Deliberately well under half — this is on screen while
    /// the player is trying to look at the world through it, and a wall you cannot see past is
    /// worse than no wall.
    /// </summary>
    private const float BaseAlpha = 0.38f;

    /// <summary>
    /// Shapes the falloff. Above 1 the fade happens low and most of the wall is faint, which keeps
    /// the volume readable without boxing the player in visually.
    /// </summary>
    private const float FadeCurve = 1.5f;

    /// <summary>
    /// Draws four gradient walls standing on the ground at the volume's edges, plus a wireframe for
    /// a crisp edge. Spheres are ignored — a sphere has no walls, and these volumes are all boxes.
    /// </summary>
    /// <param name="a">The volume. Only its X/Z extents and yaw are used for the footprint.</param>
    /// <param name="rgb">Wall colour.</param>
    /// <param name="wallHeight">
    /// How tall the drawn wall is, in yalms — <b>independent of the volume's own height</b>, which
    /// is sized for the containment test rather than for looking at.
    /// </param>
    public static void DrawGradientBox(ChallengeArea a, Vector3 rgb, float wallHeight,
                                       float thickness = 1.8f)
    {
        if (a.Shape != AreaShape.Box) return;

        try
        {
            float hx = MathF.Max(0.01f, a.SizeX * a.Scale) * 0.5f;
            float hz = MathF.Max(0.01f, a.SizeZ * a.Scale) * 0.5f;
            float h  = MathF.Max(0.5f, wallHeight);

            // Forward yaw, matching AreaOverlay.DrawBox — the inverse of the one
            // ChallengeArea.Contains applies to the delta. All three must stay in step or the wall
            // you can see stops being the volume that is tested.
            float cos = MathF.Cos(a.RotationY);
            float sin = MathF.Sin(a.RotationY);
            var   c   = a.Center;

            // The centre's Y is where the site was marked out, i.e. the player's feet — so it is
            // ground level, and the wall stands ON it rather than being centred on it.
            Vector3 Foot(float sx, float sz)
            {
                float lx = sx * hx, lz = sz * hz;
                return new Vector3(c.X + lx * cos - lz * sin, c.Y, c.Z + lx * sin + lz * cos);
            }

            var b = new[] { Foot(-1, -1), Foot(+1, -1), Foot(+1, +1), Foot(-1, +1) };

            for (int i = 0; i < 4; i++)
                DrawWall(b[i], b[(i + 1) % 4], h, rgb);

            // Outline the wall we actually drew, not the tall containment volume — a wireframe
            // twenty yalms underground would read as a second, wrong boundary.
            var visual = new ChallengeArea
            {
                Shape = AreaShape.Box,
                SizeX = a.SizeX, SizeZ = a.SizeZ, SizeY = h,
                Scale = a.Scale, RotationY = a.RotationY,
            };
            visual.SetCenter(new Vector3(c.X, c.Y + h * 0.5f, c.Z));

            AreaOverlay.DrawBox(visual, ImGui.GetColorU32(new Vector4(rgb.X, rgb.Y, rgb.Z, 0.85f)),
                                thickness);
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] gradient volume draw failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One wall, as a grid of quads fading upward. Cells whose corners will not project are skipped
    /// rather than clamped — a point behind the camera has no meaningful screen position, and
    /// stretching a quad to a garbage coordinate paints a triangle across the whole viewport.
    /// </summary>
    private static void DrawWall(Vector3 footA, Vector3 footB, float height, Vector3 rgb)
    {
        var drawList = ImGui.GetBackgroundDrawList();
        var up       = new Vector3(0f, height, 0f);

        // Corner of the cell at (column u, row v). Computed from the wall's own basis so the same
        // expression works for any yaw.
        Vector3 P(float u, float v) => Vector3.Lerp(footA, footB, u) + up * v;

        for (int r = 0; r < Rows; r++)
        {
            float v0 = r / (float)Rows;
            float v1 = (r + 1) / (float)Rows;

            float mid   = (v0 + v1) * 0.5f;
            float alpha = BaseAlpha * MathF.Pow(1f - mid, FadeCurve);
            if (alpha <= 0.004f) continue;   // below this it costs a quad and shows nothing

            uint col = ImGui.GetColorU32(new Vector4(rgb.X, rgb.Y, rgb.Z, alpha));

            for (int k = 0; k < Cols; k++)
            {
                float u0 = k / (float)Cols;
                float u1 = (k + 1) / (float)Cols;

                if (!AreaOverlay.Project(P(u0, v0), out var s0)) continue;
                if (!AreaOverlay.Project(P(u1, v0), out var s1)) continue;
                if (!AreaOverlay.Project(P(u1, v1), out var s2)) continue;
                if (!AreaOverlay.Project(P(u0, v1), out var s3)) continue;

                // Wound consistently around the quad. AddQuadFilled goes through
                // AddConvexPolyFilled, which needs a convex ring — a figure-of-eight winding
                // renders as two slivers rather than erroring.
                drawList.AddQuadFilled(s0, s1, s2, s3, col);
            }
        }
    }

    /// <summary>
    /// Paints the ground inside a site as a striped grid that follows the terrain.
    ///
    /// <para><b>The lattice is sampled once and handed in, never sampled here.</b> Draping a grid
    /// over terrain means a downward raycast per vertex, and doing that every frame is precisely
    /// the mistake <c>LimLoToolkit</c>'s ground sampler exists to warn about. A site does not move
    /// once it is marked out, so its heights are measured at placement and this method is pure
    /// drawing.</para>
    ///
    /// <para><b>It overlays the world rather than being occluded by it</b> — the background draw
    /// list is not depth-tested, so the grid reads through a rise or a rock instead of being
    /// swallowed by it. That is what makes the site legible from outside as well as inside.</para>
    ///
    /// <para>Stripes are a checkerboard of the COARSE cells, and the coarse cell is sized to the
    /// dig radius by the caller — so the pattern is not decoration, it is a picture of the
    /// granularity at which digging actually matters.</para>
    /// </summary>
    /// <param name="lattice">Fine grid of ground points, <c>[i, j]</c> spanning the site.</param>
    /// <param name="stride">Fine steps per coarse cell — the checker and line spacing.</param>
    public static void DrawGroundGrid(Vector3[,] lattice, int stride, Vector3 rgb)
    {
        if (lattice.Length == 0 || stride < 1) return;

        try
        {
            int n = lattice.GetLength(0);
            var drawList = ImGui.GetBackgroundDrawList();

            uint fill = ImGui.GetColorU32(new Vector4(rgb.X, rgb.Y, rgb.Z, 0.13f));
            uint line = ImGui.GetColorU32(new Vector4(rgb.X, rgb.Y, rgb.Z, 0.34f));

            // Checkerboard, built from the FINE quads so each coarse cell follows the ground rather
            // than being one flat plate bridging a slope.
            for (int i = 0; i + 1 < n; i++)
            {
                for (int j = 0; j + 1 < n; j++)
                {
                    if (((i / stride) + (j / stride)) % 2 != 0) continue;

                    if (!AreaOverlay.Project(lattice[i,     j],     out var s0)) continue;
                    if (!AreaOverlay.Project(lattice[i + 1, j],     out var s1)) continue;
                    if (!AreaOverlay.Project(lattice[i + 1, j + 1], out var s2)) continue;
                    if (!AreaOverlay.Project(lattice[i,     j + 1], out var s3)) continue;

                    drawList.AddQuadFilled(s0, s1, s2, s3, fill);
                }
            }

            // Coarse lines, drawn through every fine vertex so they bend with the ground. The
            // polyline breaks on a projection failure rather than joining across the gap — the same
            // rule the wireframe rings follow, and for the same reason.
            for (int c = 0; c < n; c += stride)
            {
                Polyline(drawList, lattice, c, true,  line);
                Polyline(drawList, lattice, c, false, line);
            }

            // Always close the far edges, which a stride that does not divide evenly would miss.
            if ((n - 1) % stride != 0)
            {
                Polyline(drawList, lattice, n - 1, true,  line);
                Polyline(drawList, lattice, n - 1, false, line);
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] ground grid draw failed: {ex.Message}");
        }
    }

    private static void Polyline(ImDrawListPtr drawList, Vector3[,] lattice, int index, bool alongI,
                                 uint color)
    {
        int n = lattice.GetLength(0);
        Vector2? prev = null;

        for (int k = 0; k < n; k++)
        {
            var world = alongI ? lattice[index, k] : lattice[k, index];

            if (!AreaOverlay.Project(world, out var screen)) { prev = null; continue; }

            if (prev.HasValue) drawList.AddLine(prev.Value, screen, color, 1.6f);
            prev = screen;
        }
    }

    /// <summary>
    /// A ground-level disc for a point of interest — the pieces a Surveillance dig has turned up,
    /// once they are no longer secret. Filled towards the centre and clear at the rim, so it reads
    /// as a spot on the floor rather than a bubble.
    /// </summary>
    public static void DrawGroundDisc(Vector3 centre, float radius, Vector3 rgb, float baseAlpha = 0.34f)
    {
        try
        {
            var drawList = ImGui.GetBackgroundDrawList();
            const int Segments = 32;
            const int Rings    = 6;

            // Lifted a hair off the ground: coplanar with the terrain the disc z-fights nothing
            // (this is an overlay, not depth-tested), but it does sink into any dip in the mesh.
            var c = centre + new Vector3(0f, 0.05f, 0f);

            for (int r = 0; r < Rings; r++)
            {
                float r0 = radius * r / Rings;
                float r1 = radius * (r + 1) / Rings;
                float a  = baseAlpha * (1f - (r + 0.5f) / Rings);
                if (a <= 0.004f) continue;

                uint col = ImGui.GetColorU32(new Vector4(rgb.X, rgb.Y, rgb.Z, a));

                for (int i = 0; i < Segments; i++)
                {
                    float a0 = MathF.Tau * i / Segments;
                    float a1 = MathF.Tau * (i + 1) / Segments;

                    var p0 = c + new Vector3(r0 * MathF.Cos(a0), 0f, r0 * MathF.Sin(a0));
                    var p1 = c + new Vector3(r1 * MathF.Cos(a0), 0f, r1 * MathF.Sin(a0));
                    var p2 = c + new Vector3(r1 * MathF.Cos(a1), 0f, r1 * MathF.Sin(a1));
                    var p3 = c + new Vector3(r0 * MathF.Cos(a1), 0f, r0 * MathF.Sin(a1));

                    if (!AreaOverlay.Project(p0, out var s0)) continue;
                    if (!AreaOverlay.Project(p1, out var s1)) continue;
                    if (!AreaOverlay.Project(p2, out var s2)) continue;
                    if (!AreaOverlay.Project(p3, out var s3)) continue;

                    drawList.AddQuadFilled(s0, s1, s2, s3, col);
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] ground disc draw failed: {ex.Message}");
        }
    }
}
#endif
