using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

using PanacheUI.Components;
using PanacheUI.Core;
using PanacheUI.Rendering;

namespace TieriChallengesFFXIV;

/// <summary>
/// The celebration popup. Shows
/// <c>"Moogle Time" Challenge Complete!</c> in large type with the challenge's one-line
/// descriptor beneath it, e.g. <c>#14: Have any of the moogle minions out while visiting Great
/// Moogle Bob in Gridania!</c>
///
/// Rendered with PanacheUI like the rest of the plugin. The host ImGui window carries
/// <see cref="ImGuiWindowFlags.NoInputs"/> so it never steals a click from the game underneath —
/// this is a notification, not a dialog.
///
/// Completions are queued: finishing two challenges in the same tick shows them one after the
/// other rather than dropping one.
/// </summary>
internal sealed class CompletionToast : IDisposable
{
    private const int   SurfaceW    = 560;
    private const int   SurfaceH    = 132;
    /// <summary>Set from Settings — see ProgressQueue.TotalSeconds for why this is a static.</summary>
    public static float HoldSeconds { get; set; } = 5.0f;

    private const float FadeSeconds = 0.9f;

    private static readonly PColor Accent   = PColor.FromHex("#E3B341");
    private static readonly PColor StatusOk = PColor.FromHex("#7FD6A9");
    private static readonly PColor TextHi   = PColor.White.WithOpacity(0.94f);

    /// <summary>Placeholder text standing in for an unfilled field, preview only.</summary>
    private static readonly PColor Missing  = PColor.FromHex("#E57B72");

    private readonly ITextureProvider _texProvider;

    private PanacheSurface? _surface;
    private readonly DateTime _start = DateTime.UtcNow;

    public CompletionToast(ITextureProvider texProvider) => _texProvider = texProvider;

    public void Dispose()
    {
        _surface?.Dispose();
        _surface = null;
    }

    // Queue and timing live in ToastQueue, shared with the plain-ImGui renderer so the popup
    // still appears when PanacheUI is off or unavailable.

    public void Draw(ToastQueue queue)
    {
        if (!queue.TryCurrent(ImGui.GetIO().DeltaTime, out var current, out float alpha)) return;
        var _current = current;

        var viewport = ImGui.GetMainViewport();
        var pos = new Vector2(
            viewport.Pos.X + (viewport.Size.X - SurfaceW * UiScale.Factor) * 0.5f,
            viewport.Pos.Y + viewport.Size.Y * 0.16f);

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(SurfaceW * UiScale.Factor, SurfaceH * UiScale.Factor), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        var flags = ImGuiWindowFlags.NoTitleBar
                  | ImGuiWindowFlags.NoResize
                  | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar
                  | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoFocusOnAppearing
                  | ImGuiWindowFlags.NoNav
                  | ImGuiWindowFlags.NoInputs;   // never intercept a click

        if (!ImGui.Begin("##tc_completion_toast", flags))
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

        var root = BuildTree(_current, alpha);

        float time = (float)(DateTime.UtcNow - _start).TotalSeconds;

        // No interaction: the mouse is parked far outside so nothing ever hit-tests.
        var (tex, _) = _surface.Render(root, time, new Vector2(-9999f, -9999f),
                                       false, false, 0f, ImGui.GetIO().DeltaTime,
                                       forceRedraw: false);

        if (tex.HasValue)
            ImGui.Image(tex.Value, new Vector2(physW, physH));

        ImGui.End();
    }

    private static Node BuildTree(CompletionEvent e, float alpha)
    {
        var root = new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width  = SurfaceW;
            s.HeightMode      = SizeMode.Fixed; s.Height = SurfaceH;
            s.Flow            = Flow.Horizontal;
            s.Opacity         = alpha;
            s.BackgroundColor = PColor.FromHex("#12101A").WithOpacity(0.96f * alpha);
            s.BorderRadius    = 8f;
            s.BorderColor     = Accent.WithOpacity(0.55f * alpha);
            s.BorderWidth     = 1;
        });

        // Left accent bar, matching the window chrome's identity cue.
        root.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode              = SizeMode.Fixed; s.Width = 4;
            s.HeightMode             = SizeMode.Fill;
            s.BackgroundColor        = Accent.WithOpacity(0.85f * alpha);
            s.BackgroundGradientEnd  = StatusOk.WithOpacity(0.85f * alpha);
            s.BorderRadiusTopLeft    = 8f;
            s.BorderRadiusBottomLeft = 8f;
        }));

        var body = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fill;
            s.Padding    = new EdgeSize(16, 20, 14, 20);
            s.Gap        = 4;
        });

        body.AppendChild(new Node().WithText("CHALLENGE COMPLETE").WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 10f;
            s.Bold       = true;
            s.Color      = StatusOk.WithOpacity(0.85f * alpha);
        }));

        // The headline. Quotes around the challenge name are part of the requested wording.
        // A placeholder standing in for a missing field is drawn in red.
        PColor titleColor = e.TitleMissing ? Missing : Accent;
        body.AppendChild(new Node().WithText($"“{e.Title}” Challenge Complete!").WithStyle(s =>
        {
            s.WidthMode        = SizeMode.Fill;
            s.HeightMode       = SizeMode.Fit;
            s.FontSize         = 25f;
            s.Bold             = true;
            s.Color            = titleColor.WithOpacity(alpha);
            s.TextOutlineColor = PColor.Black.WithOpacity(0.75f * alpha);
            s.TextOutlineSize  = 1.6f;
            s.TextOverflow     = TextOverflow.Ellipsis;
        }));

        body.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fill;
            s.HeightMode      = SizeMode.Fixed; s.Height = 1;
            s.Margin          = new EdgeSize(4, 0, 4, 0);
            s.BackgroundColor = Accent.WithOpacity(0.28f * alpha);
            s.BackgroundGradientEnd = PColor.Transparent;
            s.Flow            = Flow.Horizontal;
        }));

        // No "#N:" prefix. The number is a position in whatever order the list is currently
        // sorted by, so it means nothing outside the list — and the toast fires while the player
        // is looking at the game, not the list. The name is the part that identifies the thing.
        string line = e.Detail;
        PColor lineColor = e.DetailMissing ? Missing : TextHi.WithOpacity(0.80f);
        body.AppendChild(new Node().WithText(line).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 11.5f;
            s.Color        = lineColor.WithOpacity(alpha * (e.DetailMissing ? 1f : 0.80f));
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

#if DEV_BUILD
        // Dev-only renderer tag — see the matching one in FallbackToast.
        body.AppendChild(new Node().WithText("Panache").WithStyle(s =>
        {
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fit;
            s.FontSize      = 9f;
            s.TextAlign     = TextAlign.Right;
            s.Color         = PColor.FromHex("#8B8794").WithOpacity(alpha);
            s.PointerEvents = PointerEvents.None;
        }));
#endif

        root.AppendChild(body);
        return root;
    }
}
