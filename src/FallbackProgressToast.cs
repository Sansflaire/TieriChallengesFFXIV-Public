using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// The bottom-right progress notification drawn in plain ImGui, used when PanacheUI is switched
/// off or cannot be loaded.
///
/// <para>References no PanacheUI type anywhere, including in fields — same rule as
/// <see cref="FallbackToast"/> and <see cref="FallbackWindow"/>. See
/// <see cref="PanacheAvailability"/> for why that matters.</para>
///
/// <para>Keeps the reveal button, which is the part that would actually be missed. Losing
/// PanacheUI should cost the vector rendering, not the function.</para>
/// </summary>
internal sealed class FallbackProgressToast
{
    private const int Width  = 336;
    private const int Height = 104;

    private const float MarginX = 24f;
    private const float MarginY = 24f;

    private static readonly Vector4 Accent = new(0.89f, 0.70f, 0.25f, 1f);
    private static readonly Vector4 TextHi = new(0.94f, 0.94f, 0.94f, 1f);
    private static readonly Vector4 Subtle = new(0.55f, 0.55f, 0.62f, 1f);

    private readonly Action<ProgressEvent> _reveal;

    public FallbackProgressToast(Action<ProgressEvent> reveal) => _reveal = reveal;

    public void Draw(ProgressQueue queue)
    {
        if (!queue.TryCurrent(ImGui.GetIO().DeltaTime, out var e, out float alpha)) return;

        var viewport = ImGui.GetMainViewport();
        var pos = new Vector2(
            viewport.Pos.X + viewport.Size.X - Width - MarginX,
            viewport.Pos.Y + viewport.Size.Y - Height - MarginY);

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(Width, Height), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.94f * alpha);

        // No NoInputs — this popup carries a button. See ProgressToast for the trade.
        var flags = ImGuiWindowFlags.NoTitleBar
                  | ImGuiWindowFlags.NoResize
                  | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar
                  | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoFocusOnAppearing
                  | ImGuiWindowFlags.NoNav;

        ImGui.PushStyleColor(ImGuiCol.Border, Fade(Accent, 0.45f * alpha));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);

        if (ImGui.Begin("##tc_progress_toast_fallback", flags))
        {
            string title = string.IsNullOrWhiteSpace(e.Title) ? "(unnamed challenge)" : e.Title;

            ImGui.TextColored(Fade(TextHi, alpha), title);
            ImGui.TextColored(Fade(Accent, 0.95f * alpha), $"Objective  {e.Done}/{e.Total}");

            float frac = e.Total > 0 ? Math.Clamp(e.Done / (float)e.Total, 0f, 1f) : 0f;
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Fade(Accent, 0.9f * alpha));
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(1f, 1f, 1f, 0.10f * alpha));
            ImGui.ProgressBar(frac, new Vector2(-1f, 4f), string.Empty);
            ImGui.PopStyleColor(2);

            ImGui.Spacing();
            ImGui.TextColored(Fade(Subtle, alpha), "FFXIV Miscellaneous Challenges");

            // Right-align the button against the window edge.
            const float btnW = 62f;
            ImGui.SameLine();
            ImGui.SetCursorPosX(Width - btnW - 12f);

            if (ImGui.Button($"Show##tc_fb_progress_show", new Vector2(btnW, 0)))
                _reveal(e);
        }
        ImGui.End();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private static Vector4 Fade(Vector4 c, float a) => new(c.X, c.Y, c.Z, c.W * a);
}
