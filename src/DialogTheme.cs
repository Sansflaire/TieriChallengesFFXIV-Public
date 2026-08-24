using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// Shared ImGui skin for every raw-ImGui surface in this plugin, so a popup that cannot be built
/// from PanacheUI nodes still LOOKS like the rest of it.
///
/// <para><b>Standing rule, confirmed by Trist 2026-08-24: every public-facing surface in this
/// plugin must match the main window's style, whether or not it is actually rendered by
/// PanacheUI.</b> The Challenge Creator is exempt — it is dev-only, never seen by a player, and
/// DESIGN_SYSTEM §10 anti-pattern 8 already permits it to stay plain.</para>
///
/// <para><b>Why this is a plain ImGui skin and not PanacheUI itself.</b> Every surface that uses
/// it — <see cref="Dialogs"/>, <see cref="StatusWindow"/> — deliberately contains NO PanacheUI
/// type reference, so they keep working when the library is missing or switched off; that
/// contract is older than this file and more important than pixel-perfect matching. PanacheUI
/// also has no text-input component at all (verified against
/// <c>PanacheUI/src/Components/PanacheComponents.cs</c>), so the suggestion box and the
/// background-image path field could not be native Panache nodes regardless.</para>
///
/// <para><b>Where the colors come from.</b> This plugin's accent/status palette is a fixed set of
/// hex constants declared locally in <c>MainWindow</c> (<c>Accent</c>, <c>StatusOk</c>,
/// <c>Danger</c>, <c>Neutral</c>) — it is NOT read from PanacheUI's live, user-swappable theme
/// system (<c>PanacheThemes.Active</c>). Matching those fixed constants is there­fore both
/// correct (it is genuinely "the main window's style") and safe: reading the live Panache theme
/// here would require a PanacheUI reference, which is exactly what these files must not have.
/// If Trist ever wants this to track a swapped Panache theme live, that needs a different
/// architecture — say so before assuming this file should grow one.</para>
/// </summary>
internal static class DialogTheme
{
    // Same hex values as MainWindow's own constants — kept as separate literals rather than a
    // shared reference because MainWindow's copies are PColor (PanacheUI.Core) and this file must
    // import nothing from that namespace.
    public static readonly Vector4 Accent   = Hex("#E3B341");
    public static readonly Vector4 StatusOk = Hex("#7FD6A9");
    public static readonly Vector4 Danger   = Hex("#E57B72");
    public static readonly Vector4 Neutral  = Hex("#8B8794");
    public static readonly Vector4 TextHi   = new(0.94f, 0.94f, 0.94f, 1f);
    public static readonly Vector4 TextMuted = new(0.62f, 0.60f, 0.66f, 1f);

    // Dark surfaces. Not read from Panache's theme system for the reason above — chosen to sit
    // close to the dark, low-saturation panel colors the main window renders with by default.
    private static readonly Vector4 SurfaceBase  = new(0.075f, 0.070f, 0.085f, 0.98f);
    private static readonly Vector4 SurfacePanel = new(0.110f, 0.103f, 0.125f, 1f);
    private static readonly Vector4 SurfaceField = new(0.145f, 0.135f, 0.160f, 1f);

    private const int ColorCount = 15;
    private const int VarCount   = 4;

    /// <summary>
    /// Push the skin. Every call MUST be paired with <see cref="Pop"/> — imbalance here corrupts
    /// ImGui's style stack for every window drawn afterward, not just this one.
    /// </summary>
    public static void Push()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg,        SurfaceBase);
        ImGui.PushStyleColor(ImGuiCol.PopupBg,          SurfaceBase);
        ImGui.PushStyleColor(ImGuiCol.Border,           Accent with { W = 0.45f });
        ImGui.PushStyleColor(ImGuiCol.TitleBg,          SurfacePanel);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,    SurfacePanel);
        ImGui.PushStyleColor(ImGuiCol.Text,             TextHi);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled,     TextMuted);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,          SurfaceField);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,   SurfaceField with { X = SurfaceField.X + 0.03f });
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,    Accent with { W = 0.18f });
        ImGui.PushStyleColor(ImGuiCol.Button,           Accent with { W = 0.18f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,    Accent with { W = 0.32f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,     Accent with { W = 0.45f });
        ImGui.PushStyleColor(ImGuiCol.CheckMark,        Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab,       Accent);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,  5f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,  8f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding,   5f);
    }

    public static void Pop()
    {
        ImGui.PopStyleVar(VarCount);
        ImGui.PopStyleColor(ColorCount);
    }

    private static Vector4 Hex(string hex)
    {
        // Local ARGB-less hex parser rather than a PColor reference — see the class remark on
        // why this file cannot import PanacheUI.Core.
        hex = hex.TrimStart('#');
        int r = Convert.ToInt32(hex.Substring(0, 2), 16);
        int g = Convert.ToInt32(hex.Substring(2, 2), 16);
        int b = Convert.ToInt32(hex.Substring(4, 2), 16);
        return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
    }
}
