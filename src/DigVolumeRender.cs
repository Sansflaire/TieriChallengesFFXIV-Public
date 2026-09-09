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
/// <para><b>How the gradient is made.</b> ImGui's draw list has no per-vertex gradient for an
/// arbitrary quad — <c>AddRectFilledMultiColor</c> exists but only for a screen-axis-aligned
/// rectangle, and a projected wall is never one of those (perspective slants its top and bottom
/// edges even though the camera has no roll). So each wall is sliced into
/// <see cref="Bands"/> horizontal quads and each is filled at its own alpha. At this band count the
/// steps are a couple of percent of alpha apart and read as continuous.</para>
///
/// <para><b>The exact route was available and deliberately not taken.</b> The binding does expose
/// <c>PrimReserve</c>/<c>PrimWriteVtx</c>/<c>PrimWriteIdx</c>, which would give a true per-vertex
/// gradient in two triangles per wall. I could not verify their signatures in this binding — a
/// .NET 10 assembly will not load into the PowerShell available here — and writing raw vertices
/// with a wrong index count corrupts the shared background draw list, which is a graphical crash
/// rather than a wrong colour. Banded quads cannot do that. If banding is ever visible, that is the
/// upgrade, and it needs the signatures confirmed against the binding source first.</para>
///
/// <para>Painted on <c>GetBackgroundDrawList</c>, like <see cref="AreaOverlay"/> — over the game,
/// under every ImGui window, consuming no input.</para>
/// </summary>
internal static class DigVolumeRender
{
    /// <summary>
    /// Horizontal slices per wall. Higher is smoother and costs four more quads per step. Below
    /// about 16 the steps become visible as horizontal bars.
    /// </summary>
    private const int Bands = 28;

    /// <summary>
    /// Alpha at the very bottom of a wall. Deliberately well under half — this thing is on screen
    /// while the player is trying to look at the world through it, and a wall you cannot see past
    /// is worse than no wall.
    /// </summary>
    private const float BaseAlpha = 0.34f;

    /// <summary>
    /// Shapes the falloff. Above 1 the fade happens low and most of the wall is faint, which keeps
    /// the volume readable at a glance without boxing the player in visually.
    /// </summary>
    private const float FadeCurve = 1.6f;

    /// <summary>
    /// Draws the four walls of a box, bottom-opaque and top-clear, plus the wireframe on top of
    /// them for a crisp edge. Spheres are not handled — a sphere has no walls, and the volumes
    /// these tests use are all boxes.
    /// </summary>
    public static void DrawGradientBox(ChallengeArea a, Vector3 rgb, float thickness = 1.8f)
    {
        if (a.Shape != AreaShape.Box) return;

        try
        {
            float hx = MathF.Max(0.01f, a.SizeX * a.Scale) * 0.5f;
            float hy = MathF.Max(0.01f, a.SizeY * a.Scale) * 0.5f;
            float hz = MathF.Max(0.01f, a.SizeZ * a.Scale) * 0.5f;

            // Forward yaw, matching AreaOverlay.DrawBox — which is the inverse of the one
            // ChallengeArea.Contains applies to the delta. Keep all three in step or the wall you
            // can see stops being the volume that is tested.
            float cos = MathF.Cos(a.RotationY);
            float sin = MathF.Sin(a.RotationY);
            var   c   = a.Center;

            Vector3 Corner(float sx, float sy, float sz)
            {
                float lx = sx * hx, ly = sy * hy, lz = sz * hz;
                return new Vector3(c.X + lx * cos - lz * sin, c.Y + ly, c.Z + lx * sin + lz * cos);
            }

            // Bottom ring, then the top ring directly above it.
            var b = new[] { Corner(-1, -1, -1), Corner(+1, -1, -1), Corner(+1, -1, +1), Corner(-1, -1, +1) };
            var t = new[] { Corner(-1, +1, -1), Corner(+1, +1, -1), Corner(+1, +1, +1), Corner(-1, +1, +1) };

            for (int i = 0; i < 4; i++)
            {
                int n = (i + 1) % 4;
                DrawWall(b[i], b[n], t[i], t[n], rgb);
            }

            AreaOverlay.DrawBox(a, ImGui.GetColorU32(new Vector4(rgb.X, rgb.Y, rgb.Z, 0.85f)), thickness);
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] gradient volume draw failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One wall, as a stack of quads fading upward. Bands whose corners will not project are
    /// skipped rather than clamped — a point behind the camera has no meaningful screen position,
    /// and stretching a quad to a garbage coordinate paints a triangle across the whole viewport.
    /// </summary>
    private static void DrawWall(Vector3 bl, Vector3 br, Vector3 tl, Vector3 tr, Vector3 rgb)
    {
        var drawList = ImGui.GetBackgroundDrawList();

        for (int k = 0; k < Bands; k++)
        {
            float t0 = k / (float)Bands;
            float t1 = (k + 1) / (float)Bands;

            var p0 = Vector3.Lerp(bl, tl, t0);
            var p1 = Vector3.Lerp(br, tr, t0);
            var p2 = Vector3.Lerp(br, tr, t1);
            var p3 = Vector3.Lerp(bl, tl, t1);

            if (!Plugin.GameGui.WorldToScreen(p0, out var s0)) continue;
            if (!Plugin.GameGui.WorldToScreen(p1, out var s1)) continue;
            if (!Plugin.GameGui.WorldToScreen(p2, out var s2)) continue;
            if (!Plugin.GameGui.WorldToScreen(p3, out var s3)) continue;

            float mid   = (t0 + t1) * 0.5f;
            float alpha = BaseAlpha * MathF.Pow(1f - mid, FadeCurve);
            if (alpha <= 0.004f) continue;   // below this it costs a quad and shows nothing

            uint col = ImGui.GetColorU32(new Vector4(rgb.X, rgb.Y, rgb.Z, alpha));

            // Wound consistently around the quad. AddQuadFilled goes through AddConvexPolyFilled,
            // which needs a convex ring — a figure-of-eight winding renders as two slivers.
            drawList.AddQuadFilled(s0, s1, s2, s3, col);
        }
    }

    /// <summary>
    /// A ground-level disc for a point of interest — used for the pieces a Surveillance dig turns
    /// up, once they are no longer secret. Filled towards the centre and clear at the rim, so it
    /// reads as a spot on the floor rather than a bubble.
    /// </summary>
    public static void DrawGroundDisc(Vector3 centre, float radius, Vector3 rgb, float baseAlpha = 0.30f)
    {
        try
        {
            var drawList = ImGui.GetBackgroundDrawList();
            const int Segments = 32;
            const int Rings    = 6;

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

                    var p0 = centre + new Vector3(r0 * MathF.Cos(a0), 0f, r0 * MathF.Sin(a0));
                    var p1 = centre + new Vector3(r1 * MathF.Cos(a0), 0f, r1 * MathF.Sin(a0));
                    var p2 = centre + new Vector3(r1 * MathF.Cos(a1), 0f, r1 * MathF.Sin(a1));
                    var p3 = centre + new Vector3(r0 * MathF.Cos(a1), 0f, r0 * MathF.Sin(a1));

                    if (!Plugin.GameGui.WorldToScreen(p0, out var s0)) continue;
                    if (!Plugin.GameGui.WorldToScreen(p1, out var s1)) continue;
                    if (!Plugin.GameGui.WorldToScreen(p2, out var s2)) continue;
                    if (!Plugin.GameGui.WorldToScreen(p3, out var s3)) continue;

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
