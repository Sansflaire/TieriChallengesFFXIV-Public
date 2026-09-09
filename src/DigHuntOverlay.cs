#if DEV_BUILD
using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

using PanacheUI.Components;
using PanacheUI.Core;
using PanacheUI.Rendering;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. The heads-up display shared by all three dig tests: one line saying how
/// warm the player is, one line of flavour, the running clock, and — for the Clue Trail — the
/// pulsing radar ring.
///
/// <para>Parked at the top of the screen rather than the bottom because the bottom corners already
/// belong to the completion and progress toasts, and a test is live for minutes at a time. It
/// cannot sit where a transient popup will land on top of it.</para>
///
/// <para>The host window carries <see cref="ImGuiWindowFlags.NoInputs"/>. This is on screen for a
/// whole run while the player is running and turning the camera, so a rectangle that swallowed
/// clicks would be genuinely hostile. It has no controls: the tests are driven from the lab window
/// and from chat.</para>
///
/// <para>Nothing here decides anything — every value is read off the active <see cref="IDigTest"/>.
/// </para>
/// </summary>
internal sealed class DigHuntOverlay : IDisposable
{
    private const int SurfaceW = 420;
    private const int SurfaceH = 82;

    /// <summary>Radar surface, drawn just under the banner. Square, so the ring is a circle.</summary>
    private const int RadarSize = 96;

    /// <summary>
    /// Distance down from the top of the viewport, as a fraction of its height. Low enough to clear
    /// the default target bar, high enough to stay out of the way of a fight.
    /// </summary>
    private const float TopFraction = 0.16f;

    private static readonly PColor Ink    = PColor.FromHex("#12101A");
    private static readonly PColor Cold   = PColor.FromHex("#8C8AA0");
    private static readonly PColor Red    = PColor.FromHex("#E5484D");
    private static readonly PColor Yellow = PColor.FromHex("#E3B341");
    private static readonly PColor Green  = PColor.FromHex("#7FD6A9");
    private static readonly PColor DigCol = PColor.FromHex("#FFCC33");

    private readonly ITextureProvider _texProvider;
    private readonly DateTime         _start = DateTime.UtcNow;

    private PanacheSurface? _banner;
    private PanacheSurface? _radar;

    public DigHuntOverlay(ITextureProvider texProvider) => _texProvider = texProvider;

    public void Dispose()
    {
        _banner?.Dispose(); _banner = null;
        _radar?.Dispose();  _radar  = null;
    }

    public void Draw(DigTests tests)
    {
        var active = tests.Active;
        if (active == null) return;

        float time = (float)(DateTime.UtcNow - _start).TotalSeconds;

        DrawBanner(active, time);

        if (active.RadarCloseness is { } closeness)
            DrawRadar(active, closeness, time);
    }

    // ── the banner ───────────────────────────────────────────────────────────

    private void DrawBanner(IDigTest test, float time)
    {
        float uiScale = UiScale.Factor;
        int   physW   = (int)(SurfaceW * uiScale);
        int   physH   = (int)(SurfaceH * uiScale);

        var viewport = ImGui.GetMainViewport();
        var pos = new Vector2(
            viewport.Pos.X + (viewport.Size.X - physW) * 0.5f,
            viewport.Pos.Y + viewport.Size.Y * TopFraction);

        if (!BeginHud("##tc_dig_banner", pos, physW, physH)) return;

        // Same trick as the toasts: the surface grows physically with the UI scale and lays out
        // against fixed logical dimensions, so BuildBanner stays unaware of the scale entirely.
        _banner ??= new PanacheSurface(_texProvider, physW, physH);
        _banner.Resize(physW, physH);
        _banner.Scale = uiScale;

        var (tex, _) = _banner.Render(BuildBanner(test, time), time, Vector2.Zero, false, false,
                                      0f, ImGui.GetIO().DeltaTime, forceRedraw: false);

        if (tex.HasValue) ImGui.Image(tex.Value, new Vector2(physW, physH));
        ImGui.End();
    }

    private static Node BuildBanner(IDigTest test, float time)
    {
        var band   = test.Band;
        var accent = AccentFor(band);

        // Only the call to action pulses. A HUD element that breathes for the whole run is noise;
        // one that starts moving the moment you are standing on the spot is a signal.
        float pulse = band == DigBand.Dig
                        ? 0.78f + 0.22f * MathF.Sin(time * 6f)
                        : 1f;

        var root = new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width  = SurfaceW;
            s.HeightMode      = SizeMode.Fixed; s.Height = SurfaceH;
            s.Flow            = Flow.Horizontal;
            s.BackgroundColor = Ink.WithOpacity(0.94f);
            s.BorderRadius    = 8f;
            s.BorderColor     = accent.WithOpacity(0.45f * pulse);
            s.BorderWidth     = 1;
        });

        root.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode              = SizeMode.Fixed; s.Width = 3;
            s.HeightMode             = SizeMode.Fill;
            s.BackgroundColor        = accent.WithOpacity(0.85f * pulse);
            s.BorderRadiusTopLeft    = 8f;
            s.BorderRadiusBottomLeft = 8f;
        }));

        var body = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fill;
            s.Padding    = new EdgeSize(9, 14, 8, 14);
            s.Gap        = 2;
        });

        body.AppendChild(new Node().WithText(test.Headline).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 25f;
            s.Bold         = true;
            s.Color        = accent.WithOpacity(pulse);
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

        // Flavour left, clock right. Both on this row rather than the clock sharing the headline's
        // row, so the two never have to be vertically aligned at wildly different font sizes.
        var footer = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Gap        = 8;
        });

        footer.AppendChild(new Node().WithText(test.Subtitle).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 12.5f;
            s.Color        = Theme.TextMuted;
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

        // Fixed width plus TextAlign.Right is how the framework's own components right-align a
        // value — a Fit width would hug the text and leave the clock drifting as digits change.
        footer.AppendChild(new Node()
            .WithText(CompletionStore.FormatRaceTime(test.ElapsedSeconds))
            .WithStyle(s =>
            {
                s.WidthMode  = SizeMode.Fixed; s.Width = 92;
                s.HeightMode = SizeMode.Fit;
                s.FontSize   = 12.5f;
                s.Bold       = true;
                s.Color      = accent.WithOpacity(0.95f);
                s.TextAlign  = TextAlign.Right;
            }));

        body.AppendChild(footer);
        root.AppendChild(body);
        return root;
    }

    // ── the radar ────────────────────────────────────────────────────────────

    /// <summary>
    /// The Clue Trail's ring. Pulse frequency scales with closeness and the fill goes solid the
    /// moment a dig would land — so "solid" is a promise the player can act on, not a decoration.
    /// </summary>
    private void DrawRadar(IDigTest test, float closeness, float time)
    {
        float uiScale = UiScale.Factor;
        int   phys    = (int)(RadarSize * uiScale);

        var viewport = ImGui.GetMainViewport();
        var pos = new Vector2(
            viewport.Pos.X + (viewport.Size.X - phys) * 0.5f,
            viewport.Pos.Y + viewport.Size.Y * TopFraction + SurfaceH * uiScale + 8f);

        if (!BeginHud("##tc_dig_radar", pos, phys, phys)) return;

        _radar ??= new PanacheSurface(_texProvider, phys, phys);
        _radar.Resize(phys, phys);
        _radar.Scale = uiScale;

        var (tex, _) = _radar.Render(BuildRadar(closeness, time), time, Vector2.Zero, false, false,
                                     0f, ImGui.GetIO().DeltaTime, forceRedraw: false);

        if (tex.HasValue) ImGui.Image(tex.Value, new Vector2(phys, phys));
        ImGui.End();
    }

    private static Node BuildRadar(float closeness, float time)
    {
        bool solid = closeness >= 1f;

        // 1.2 Hz at the outer edge climbing to about 7 Hz just before the spot. Scaling frequency
        // rather than size is what makes this read as a detector rather than as a progress bar —
        // the player hears the rhythm change before they consciously read the ring.
        float hz   = 1.2f + closeness * 5.8f;
        float wave = 0.5f + 0.5f * MathF.Sin(time * MathF.Tau * hz);

        // Never fully transparent even at the outer edge: a ring that vanishes between beats reads
        // as a bug rather than as a slow pulse.
        float fill = solid ? 1f : 0.18f + 0.62f * wave;

        var accent = solid ? DigCol : Green;

        var root = new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width  = RadarSize;
            s.HeightMode      = SizeMode.Fixed; s.Height = RadarSize;
            s.BackgroundColor = PColor.White.WithOpacity(0f);
        });

        // A square node with a radius of half its side is a circle — the framework has no circle
        // primitive and does not need one.
        root.AppendChild(new Node().WithStyle(s =>
        {
            s.Position        = PositionMode.Absolute;
            s.Left            = 0;
            s.Top             = 0;
            s.WidthMode       = SizeMode.Fixed; s.Width  = RadarSize;
            s.HeightMode      = SizeMode.Fixed; s.Height = RadarSize;
            s.BorderRadius    = RadarSize / 2f;
            s.BackgroundColor = Ink.WithOpacity(0.55f);
            s.BorderColor     = accent.WithOpacity(0.90f);
            s.BorderWidth     = 2;
            s.PointerEvents   = PointerEvents.None;
        }));

        // The pulsing core, inset so the outer ring always stays legible as a boundary.
        const float Inset = 14f;
        const float Core  = RadarSize - Inset * 2f;

        root.AppendChild(new Node().WithStyle(s =>
        {
            s.Position        = PositionMode.Absolute;
            s.Left            = Inset;
            s.Top             = Inset;
            s.WidthMode       = SizeMode.Fixed; s.Width  = Core;
            s.HeightMode      = SizeMode.Fixed; s.Height = Core;
            s.BorderRadius    = Core / 2f;
            s.BackgroundColor = accent.WithOpacity(fill);
            s.PointerEvents   = PointerEvents.None;
        }));

        return root;
    }

    // ── shared host-window plumbing ──────────────────────────────────────────

    private static bool BeginHud(string id, Vector2 pos, int w, int h)
    {
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(w, h), ImGuiCond.Always);
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

        if (ImGui.Begin(id, flags)) return true;

        ImGui.End();
        return false;
    }

    private static PColor AccentFor(DigBand band) => band switch
    {
        DigBand.Dig    => DigCol,
        DigBand.Green  => Green,
        DigBand.Yellow => Yellow,
        DigBand.Red    => Red,
        _              => Cold,
    };
}
#endif
