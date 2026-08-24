using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// The completion popup drawn in plain ImGui, used when PanacheUI is switched off or cannot be
/// loaded.
///
/// <para>References no PanacheUI type anywhere, including in fields — same rule as
/// <see cref="FallbackWindow"/>. See <see cref="PanacheAvailability"/> for why that matters.</para>
///
/// <para>Deliberately still a celebration rather than a plain notice: big scaled headline, accent
/// colouring, a rule, and the numbered descriptor. Turning PanacheUI off should cost you the
/// vector rendering, not the moment.</para>
/// </summary>
internal sealed class FallbackToast
{
    private const int Width  = 560;
    private const int Height = 132;

    private static readonly Vector4 Accent  = new(0.89f, 0.70f, 0.25f, 1f);
    private static readonly Vector4 Ok      = new(0.50f, 0.84f, 0.66f, 1f);
    private static readonly Vector4 Missing = new(0.90f, 0.48f, 0.45f, 1f);
    private static readonly Vector4 TextHi  = new(0.94f, 0.94f, 0.94f, 1f);

    public void Draw(ToastQueue queue)
    {
        if (!queue.TryCurrent(ImGui.GetIO().DeltaTime, out var e, out float alpha)) return;

        var viewport = ImGui.GetMainViewport();
        var pos = new Vector2(
            viewport.Pos.X + (viewport.Size.X - Width) * 0.5f,
            viewport.Pos.Y + viewport.Size.Y * 0.16f);

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(Width, Height), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.94f * alpha);

        var flags = ImGuiWindowFlags.NoTitleBar
                  | ImGuiWindowFlags.NoResize
                  | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar
                  | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoFocusOnAppearing
                  | ImGuiWindowFlags.NoNav
                  | ImGuiWindowFlags.NoInputs;   // never intercept a click

        ImGui.PushStyleColor(ImGuiCol.Border, Fade(Accent, 0.55f * alpha));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);

        if (ImGui.Begin("##tc_completion_toast_fallback", flags))
        {
            ImGui.TextColored(Fade(Ok, 0.85f * alpha), "CHALLENGE COMPLETE");

            // ImGui has one font size, so the headline is scaled rather than re-styled.
            ImGui.SetWindowFontScale(1.7f);
            ImGui.TextColored(Fade(e.TitleMissing ? Missing : Accent, alpha),
                              $"“{e.Title}” Challenge Complete!");
            ImGui.SetWindowFontScale(1.0f);

            ImGui.Separator();

            // No number: it is a position in whichever list you were looking at, and a
            // notification has no list. See MainWindow.BuildDetail.
            string line = e.Detail;
            ImGui.PushStyleColor(ImGuiCol.Text,
                Fade(e.DetailMissing ? Missing : TextHi, (e.DetailMissing ? 1f : 0.8f) * alpha));
            ImGui.TextWrapped(line);
            ImGui.PopStyleColor();

#if DEV_BUILD
            // Dev-only renderer tag. The fallback is styled closely enough to the Panache toast
            // that telling them apart by eye is genuinely hard — this removes the doubt.
            var corner = new Vector2(ImGui.GetWindowPos().X + Width - 74,
                                     ImGui.GetWindowPos().Y + Height - 22);
            ImGui.GetWindowDrawList().AddText(corner,
                ImGui.GetColorU32(Fade(new Vector4(0.55f, 0.55f, 0.62f, 1f), alpha)),
                "ImGui");
#endif
        }
        ImGui.End();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private static Vector4 Fade(Vector4 c, float a) => new(c.X, c.Y, c.Z, c.W * a);
}
