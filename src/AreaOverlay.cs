#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. Draws trigger volumes into the world so they can actually be placed by
/// eye instead of by guessing at coordinates.
///
/// Uses <c>ImGui.GetBackgroundDrawList()</c> + <c>IGameGui.WorldToScreen</c> — the same approach
/// EasterEvent uses for its AoE rings. The background draw list paints over the game but under
/// every ImGui window, so the creator window is never occluded by the overlay, and nothing here
/// consumes input.
///
/// <para><b>Projection breaks are load-bearing.</b> WorldToScreen returns false for points behind
/// the camera. Every polyline resets its "previous point" on a failure instead of joining across
/// the gap — otherwise a volume that is half behind you draws a wild line across the screen.</para>
/// </summary>
internal sealed class AreaOverlay
{
    /// <summary>Master toggle, surfaced in the creator.</summary>
    public bool Enabled = true;

    /// <summary>Also outline volumes belonging to already-saved challenges in this zone.</summary>
    public bool ShowSaved = true;

    private const int RingSegments = 48;

    private static uint Col(float r, float g, float b, float a) =>
        ImGui.GetColorU32(new Vector4(r, g, b, a));

    public void Draw(Configuration config, CompletionStore store,
                     IReadOnlyList<ChallengeArea> draftAreas, int selectedIndex)
    {
        if (!Enabled) return;

        try
        {
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp == null) return;

            Vector3 playerPos = lp.Position;

            // Saved volumes first, so the draft always paints on top of them.
            if (ShowSaved)
            {
                ushort territory = (ushort)Plugin.ClientState.TerritoryType;
                foreach (var ch in config.CustomChallenges)
                {
                    if (ch.TerritoryId != territory) continue;

                    bool complete = store.IsComplete(ch.Id);
                    // Completed challenges dim right down — they are context, not the subject.
                    float alpha = complete ? 0.20f : 0.45f;
                    uint c = complete
                        ? Col(0.50f, 0.84f, 0.66f, alpha)
                        : Col(0.55f, 0.55f, 0.70f, alpha);
                    uint labelCol = Col(0.75f, 0.75f, 0.85f, alpha + 0.25f);

                    // Each kind keeps its volumes somewhere different: the legacy kinds in Areas,
                    // the composite kind inside its Requirements, a race in its three named roles.
                    // All three are drawn — a saved volume invisible while authoring next to it is
                    // exactly the context this overlay exists to give.
                    for (int i = 0; i < ch.Areas.Count; i++)
                    {
                        DrawArea(ch.Areas[i], c, 1.5f);
                        DrawLabel(ch.Areas[i], $"{ch.Title} · {i + 1}", labelCol);
                    }

                    if (ch.Requirements != null)
                    {
                        for (int i = 0; i < ch.Requirements.Count; i++)
                        {
                            var area = ch.Requirements[i].Area;
                            if (area == null) continue;
                            DrawArea(area, c, 1.5f);
                            DrawLabel(area, $"{ch.Title} · {i + 1}", labelCol);
                        }
                    }

                    if (ch.Kind == ChallengeKind.RaceTimer)
                    {
                        if (ch.RaceStart  != null) { DrawArea(ch.RaceStart,  c, 1.5f); DrawLabel(ch.RaceStart,  $"{ch.Title} · start",  labelCol); }
                        if (ch.RaceFinish != null) { DrawArea(ch.RaceFinish, c, 1.5f); DrawLabel(ch.RaceFinish, $"{ch.Title} · finish", labelCol); }
                        if (ch.RaceUseQuitArea && ch.RaceQuit != null)
                        {
                            DrawArea(ch.RaceQuit, c, 1.0f);
                            DrawLabel(ch.RaceQuit, $"{ch.Title} · bounds", labelCol);
                        }
                    }
                }
            }

            // The draft being authored.
            for (int i = 0; i < draftAreas.Count; i++)
            {
                var a = draftAreas[i];
                bool inside   = a.Contains(playerPos);
                bool selected = i == selectedIndex;

                // Green while you are standing inside it — the single most useful signal when
                // dialling in a volume. Gold when selected, amber otherwise.
                uint color = inside
                    ? Col(0.44f, 0.86f, 0.62f, 0.95f)
                    : selected
                        ? Col(0.89f, 0.70f, 0.25f, 0.95f)
                        : Col(0.85f, 0.60f, 0.30f, 0.70f);

                DrawArea(a, color, selected ? 2.6f : 1.8f);
                DrawCentreMarker(a, color);

                float dist = Vector3.Distance(playerPos, a.Center);
                DrawLabel(a, $"{i + 1}. {a.Name}  ({a.Describe()})  {dist:0.#}y", color);
            }
        }
        catch (Exception ex)
        {
            Diag.Error(ex, "AreaOverlay draw failed");
        }
    }

    private static void DrawArea(ChallengeArea a, uint color, float thickness)
    {
        if (a.Shape == AreaShape.Sphere) DrawSphere(a, color, thickness);
        else                             DrawBox(a, color, thickness);
    }

    /// <summary>
    /// Three orthogonal great circles. A single ground ring reads as a flat disc and hides the
    /// fact that a sphere has vertical extent — which matters, because the containment test is
    /// fully 3D and a player standing above the volume is NOT inside it.
    /// </summary>
    private static void DrawSphere(ChallengeArea a, uint color, float thickness)
    {
        float r = a.EffectiveRadius;
        var c = a.Center;

        // Equator (XZ), then the two vertical rings.
        DrawRing(c, r, thickness, color, (ang, rad) => new Vector3(rad * MathF.Cos(ang), 0f, rad * MathF.Sin(ang)));
        DrawRing(c, r, thickness * 0.7f, color, (ang, rad) => new Vector3(rad * MathF.Cos(ang), rad * MathF.Sin(ang), 0f));
        DrawRing(c, r, thickness * 0.7f, color, (ang, rad) => new Vector3(0f, rad * MathF.Sin(ang), rad * MathF.Cos(ang)));
    }

    private static void DrawRing(Vector3 centre, float radius, float thickness, uint color,
                                 Func<float, float, Vector3> offset)
    {
        var drawList = ImGui.GetBackgroundDrawList();
        Vector2? prev = null;

        for (int i = 0; i <= RingSegments; i++)
        {
            float ang = MathF.Tau * i / RingSegments;
            var world = centre + offset(ang, radius);

            if (!Plugin.GameGui.WorldToScreen(world, out var screen))
            {
                prev = null;                       // behind the camera — break the polyline
                continue;
            }

            if (prev.HasValue) drawList.AddLine(prev.Value, screen, color, thickness);
            prev = screen;
        }
    }

    /// <summary>All twelve edges of the (optionally yawed) box.</summary>
    private static void DrawBox(ChallengeArea a, uint color, float thickness)
    {
        float hx = MathF.Max(0.01f, a.SizeX * a.Scale) * 0.5f;
        float hy = MathF.Max(0.01f, a.SizeY * a.Scale) * 0.5f;
        float hz = MathF.Max(0.01f, a.SizeZ * a.Scale) * 0.5f;

        // Forward yaw here is the inverse of the one ChallengeArea.Contains applies to the
        // delta — keep the two in step or the drawn box will not match the tested box.
        float cos = MathF.Cos(a.RotationY);
        float sin = MathF.Sin(a.RotationY);
        var centre = a.Center;

        Vector3 Corner(float sx, float sy, float sz)
        {
            float lx = sx * hx, ly = sy * hy, lz = sz * hz;
            float wx = lx * cos - lz * sin;
            float wz = lx * sin + lz * cos;
            return new Vector3(centre.X + wx, centre.Y + ly, centre.Z + wz);
        }

        // 0-3 bottom (y = -hy), 4-7 top.
        var c = new Vector3[8];
        c[0] = Corner(-1, -1, -1); c[1] = Corner(+1, -1, -1);
        c[2] = Corner(+1, -1, +1); c[3] = Corner(-1, -1, +1);
        c[4] = Corner(-1, +1, -1); c[5] = Corner(+1, +1, -1);
        c[6] = Corner(+1, +1, +1); c[7] = Corner(-1, +1, +1);

        for (int i = 0; i < 4; i++)
        {
            int n = (i + 1) % 4;
            DrawSegment(c[i],     c[n],     color, thickness);          // bottom face
            DrawSegment(c[i + 4], c[n + 4], color, thickness);          // top face
            DrawSegment(c[i],     c[i + 4], color, thickness * 0.7f);   // vertical
        }
    }

    private static void DrawSegment(Vector3 a, Vector3 b, uint color, float thickness)
    {
        if (!Plugin.GameGui.WorldToScreen(a, out var sa)) return;
        if (!Plugin.GameGui.WorldToScreen(b, out var sb)) return;
        ImGui.GetBackgroundDrawList().AddLine(sa, sb, color, thickness);
    }

    /// <summary>Small cross at the exact centre, so "move to me" placement is unambiguous.</summary>
    private static void DrawCentreMarker(ChallengeArea a, uint color)
    {
        if (!Plugin.GameGui.WorldToScreen(a.Center, out var p)) return;

        var drawList = ImGui.GetBackgroundDrawList();
        const float k = 6f;
        drawList.AddLine(new Vector2(p.X - k, p.Y), new Vector2(p.X + k, p.Y), color, 2f);
        drawList.AddLine(new Vector2(p.X, p.Y - k), new Vector2(p.X, p.Y + k), color, 2f);
    }

    private static void DrawLabel(ChallengeArea a, string text, uint color)
    {
        // Anchor the label at the top of the volume so it does not sit inside the wireframe.
        float top = a.Shape == AreaShape.Sphere
            ? a.EffectiveRadius
            : MathF.Max(0.01f, a.SizeY * a.Scale) * 0.5f;

        var anchor = a.Center + new Vector3(0f, top + 0.35f, 0f);
        if (!Plugin.GameGui.WorldToScreen(anchor, out var p)) return;

        var drawList = ImGui.GetBackgroundDrawList();
        var size = ImGui.CalcTextSize(text);
        var pos  = new Vector2(p.X - size.X * 0.5f, p.Y - size.Y);

        // Cheap readability backdrop — world geometry behind the text is arbitrary.
        drawList.AddRectFilled(new Vector2(pos.X - 4f, pos.Y - 2f),
                               new Vector2(pos.X + size.X + 4f, pos.Y + size.Y + 2f),
                               ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f)), 3f);
        drawList.AddText(pos, color, text);
    }
}
#endif
