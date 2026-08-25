using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

using PanacheUI.Components;
using PanacheUI.Core;
using PanacheUI.Rendering;

namespace TieriChallengesFFXIV;

/// <summary>
/// The small bottom-right progress notification: challenge number and name, how far along it is,
/// the plugin's own name in small type, and a button that opens the main window with this
/// challenge revealed.
///
/// <para><b>This popup takes input</b>, unlike <see cref="CompletionToast"/>. That one is pure
/// celebration and carries <see cref="ImGuiWindowFlags.NoInputs"/> so it can never eat a click
/// aimed at the game; this one has a button, so it must accept the mouse. It is small and parked
/// in the corner to keep that trade honest — a click-absorbing rectangle in the middle of the
/// screen would be hostile.</para>
/// </summary>
internal sealed class ProgressToast : IDisposable
{
    private const int SurfaceW = 336;
    private const int SurfaceH = 104;

    /// <summary>Gap from the viewport's bottom-right corner. Clears the default hotbar area.</summary>
    private const float MarginX = 24f;
    private const float MarginY = 24f;

    private const float AccentBarW = 3f;
    private const float PadLeft    = 12f;
    private const float PadRight   = 13f;
    private const float BarH       = 4f;

    /// <summary>Usable width inside the accent bar and padding — the progress fill's 100% span.</summary>
    private const float BarW = SurfaceW - AccentBarW - PadLeft - PadRight;

    private static readonly PColor Accent   = PColor.FromHex("#E3B341");
    private static readonly PColor StatusOk = PColor.FromHex("#7FD6A9");
    private static readonly PColor TextHi   = PColor.White.WithOpacity(0.94f);

    private readonly ITextureProvider _texProvider;
    private readonly Action<ProgressEvent> _reveal;

    private PanacheSurface? _surface;
    private readonly DateTime _start = DateTime.UtcNow;


    public ProgressToast(ITextureProvider texProvider, Action<ProgressEvent> reveal)
    {
        _texProvider = texProvider;
        _reveal      = reveal;
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
    }

    public void Draw(ProgressQueue queue)
    {
        if (!queue.TryCurrent(ImGui.GetIO().DeltaTime, out var e, out float alpha)) return;

        var viewport = ImGui.GetMainViewport();
        var pos = new Vector2(
            viewport.Pos.X + viewport.Size.X - SurfaceW - MarginX,
            viewport.Pos.Y + viewport.Size.Y - SurfaceH - MarginY);

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(SurfaceW * UiScale.Factor, SurfaceH * UiScale.Factor), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        // No NoInputs here — see the class remark. NoFocusOnAppearing keeps the popup from
        // stealing keyboard focus mid-fight just because a step landed.
        var flags = ImGuiWindowFlags.NoTitleBar
                  | ImGuiWindowFlags.NoResize
                  | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar
                  | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoFocusOnAppearing
                  | ImGuiWindowFlags.NoNav;

        if (!ImGui.Begin("##tc_progress_toast", flags))
        {
            ImGui.End();
            return;
        }

        // The toast grows PHYSICALLY with the scale rather than laying its content out into the
        // same pixel box — a fixed-size surface at scale 3 would simply crop the text. Logical
        // dimensions therefore stay SurfaceWxSurfaceH, so BuildTree below is unchanged and unaware.
        float uiScale = UiScale.Factor;
        int   physW   = (int)(SurfaceW * uiScale);
        int   physH   = (int)(SurfaceH * uiScale);

        _surface ??= new PanacheSurface(_texProvider, physW, physH);
        _surface.Resize(physW, physH);
        _surface.Scale = uiScale;

        var root = BuildTree(e, alpha);

        var origin     = ImGui.GetCursorScreenPos();
        var mouse      = ImGui.GetMousePos();
        var localMouse = new Vector2(mouse.X - origin.X, mouse.Y - origin.Y);

        bool mouseDown  = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        bool mouseClick = ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                       && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows
                                              | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                              | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);

        float time = (float)(DateTime.UtcNow - _start).TotalSeconds;

        var (tex, _) = _surface.Render(root, time, localMouse, mouseDown, mouseClick,
                                       0f, ImGui.GetIO().DeltaTime, forceRedraw: false);

        if (tex.HasValue)
            ImGui.Image(tex.Value, new Vector2(physW, physH));

        ImGui.End();
    }

    private Node BuildTree(ProgressEvent e, float alpha)
    {
        var root = new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width  = SurfaceW;
            s.HeightMode      = SizeMode.Fixed; s.Height = SurfaceH;
            s.Flow            = Flow.Horizontal;
            s.Opacity         = alpha;
            s.BackgroundColor = PColor.FromHex("#12101A").WithOpacity(0.96f * alpha);
            s.BorderRadius    = 8f;
            s.BorderColor     = Accent.WithOpacity(0.45f * alpha);
            s.BorderWidth     = 1;
        });

        // Left accent bar — the same identity cue the window chrome and the completion toast use.
        root.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode              = SizeMode.Fixed; s.Width = 3;
            s.HeightMode             = SizeMode.Fill;
            s.BackgroundColor        = Accent.WithOpacity(0.85f * alpha);
            s.BorderRadiusTopLeft    = 8f;
            s.BorderRadiusBottomLeft = 8f;
        }));

        var body = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fill;
            s.Padding    = new EdgeSize(10, 12, 9, 13);
            s.Gap        = 3;
        });

        // Challenge number and name.
        string title = string.IsNullOrWhiteSpace(e.Title) ? "(unnamed challenge)" : e.Title;

        body.AppendChild(new Node().WithText(title).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 12.5f;
            s.Bold         = true;
            s.Color        = TextHi.WithOpacity(alpha);
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

        // Progress: the count plus a bar, so it reads at a glance without parsing the numbers.
        float frac = e.Total > 0 ? Math.Clamp(e.Done / (float)e.Total, 0f, 1f) : 0f;

        body.AppendChild(new Node().WithText($"Objective  {e.Done}/{e.Total}").WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 11f;
            s.Bold       = true;
            s.Color      = Accent.WithOpacity(0.95f * alpha);
        }));

        var track = new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fill;
            s.HeightMode      = SizeMode.Fixed; s.Height = BarH;
            s.Margin          = new EdgeSize(1, 0, 3, 0);
            s.BackgroundColor = PColor.White.WithOpacity(0.10f * alpha);
            s.BorderRadius    = 2f;
            s.PointerEvents   = PointerEvents.None;
        });

        // Absolutely positioned, so the fill needs a real pixel width rather than a Fill —
        // the same shape as MainWindow.ProgressBar. There is no percentage SizeMode.
        track.AppendChild(new Node().WithStyle(s =>
        {
            s.Position              = PositionMode.Absolute;
            s.Left                  = 0;
            s.Top                   = 0;
            s.WidthMode             = SizeMode.Fixed; s.Width  = Math.Max(0f, BarW * frac);
            s.HeightMode            = SizeMode.Fixed; s.Height = BarH;
            s.BackgroundColor       = Accent.WithOpacity(0.90f * alpha);
            s.BackgroundGradientEnd = StatusOk.WithOpacity(0.90f * alpha);
            s.BorderRadius          = 2f;
            s.PointerEvents         = PointerEvents.None;
        }));
        body.AppendChild(track);

        // Footer: plugin name in small type on the left, reveal button on the right.
        var footer = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Gap        = 8;
        });

        footer.AppendChild(new Node().WithText("FFXIV Miscellaneous Challenges").WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.Margin       = new EdgeSize(6, 0, 0, 0);
            s.FontSize     = 9f;
            s.Color        = Theme.TextSubtle.WithOpacity(alpha);
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

        var captured = e;
        var button = PUI.PillButton("progress_show", "Show", Accent);

        // Declared on the node, cross-faded by the renderer — the two-field hover latch this
        // used to carry is gone, and with it the frame of lag it introduced.
        button.WithStyle(s => s.HoverBackgroundColor = Accent.WithOpacity(0.32f));
        button.OnClick += _ => _reveal(captured);

        footer.AppendChild(button);
        body.AppendChild(footer);

        root.AppendChild(body);
        return root;
    }
}
