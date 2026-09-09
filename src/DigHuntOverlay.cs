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

    /// <summary>
    /// Accumulated pulse phase, advanced by frequency × frame time.
    ///
    /// <para><b>It must be integrated, not computed from absolute time.</b> The first version used
    /// <c>sin(time × hz)</c>, and because <c>time</c> is seconds since the overlay was created, a
    /// small change in <c>hz</c> produced an enormous jump in phase — five minutes in, nudging the
    /// rate from 3.0 to 3.1 Hz leaps thirty whole cycles. So walking towards the spot, which is
    /// exactly when <c>hz</c> changes every frame, made the light strobe erratically instead of
    /// speeding up smoothly. Integrating means changing the rate changes only the rate.</para>
    /// </summary>
    private float _radarPhase;

    /// <summary>
    /// Radar opacity envelope, 0 (invisible) to 1 (full). Ramps towards whichever the range says.
    ///
    /// <para><b>A value, not an animation.</b> A triggered fade would have to start, and therefore
    /// could be restarted — walking in and out of range would relaunch it from 0 each time and the
    /// ring would snap. This just climbs while in range and falls while out, so crossing the
    /// boundary repeatedly makes it wander up and down from wherever it currently is. There is no
    /// timer to reset because there is no timer.</para>
    /// </summary>
    private float _radarFade;

    /// <summary>
    /// Last known closeness, held through the fade-out. Once out of range there is no closeness to
    /// read, and the ring still has two seconds of life left — freezing the rate it had on the way
    /// out is less distracting than snapping it to the slowest.
    /// </summary>
    private float _radarCloseness;

    public DigHuntOverlay(ITextureProvider texProvider) => _texProvider = texProvider;

    public void Dispose()
    {
        _banner?.Dispose(); _banner = null;
        _radar?.Dispose();  _radar  = null;
    }

    public void Draw(DigTests tests)
    {
        var active = tests.Active;

        if (active == null)
        {
            // Nothing running: the next test starts from silence rather than inheriting a
            // half-faded ring from the last one.
            _radarFade  = 0f;
            _radarPhase = 0f;
            return;
        }

        float time = (float)(DateTime.UtcNow - _start).TotalSeconds;

        DrawBanner(active, time);

        var  closeness = active.RadarCloseness;
        bool inRange   = closeness.HasValue;

        if (inRange) _radarCloseness = closeness!.Value;

        // Linear ramp towards the target, so a full fade takes exactly the configured time and a
        // partial one takes proportionally less — which is what makes crossing the boundary twice
        // in quick succession look like one continuous movement rather than two events.
        float seconds = MathF.Max(0.05f, DigTuning.RadarFadeSeconds);
        float step    = ImGui.GetIO().DeltaTime / seconds;
        float target  = inRange ? 1f : 0f;

        if      (_radarFade < target) _radarFade = MathF.Min(target, _radarFade + step);
        else if (_radarFade > target) _radarFade = MathF.Max(target, _radarFade - step);

        if (_radarFade <= 0.002f)
        {
            _radarFade  = 0f;
            _radarPhase = 0f;      // fully gone — next appearance starts from a known point
            return;
        }

        DrawRadar(_radarCloseness, _radarFade);
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
        EndHud();
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
    private void DrawRadar(float closeness, float fade)
    {
        // Advance the phase by this frame's share of the current rate. Wrapped so the accumulator
        // cannot drift into float values where sin loses precision over a long session.
        float hz = MinPulseHz + closeness * (MaxPulseHz - MinPulseHz);

        _radarPhase += ImGui.GetIO().DeltaTime * MathF.Tau * hz;
        if (_radarPhase > MathF.Tau) _radarPhase %= MathF.Tau;

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

        float time = (float)(DateTime.UtcNow - _start).TotalSeconds;

        var (tex, _) = _radar.Render(BuildRadar(closeness, _radarPhase, fade), time, Vector2.Zero,
                                     false, false, 0f, ImGui.GetIO().DeltaTime, forceRedraw: false);

        if (tex.HasValue) ImGui.Image(tex.Value, new Vector2(phys, phys));
        EndHud();
    }

    /// <summary>
    /// Breathing room between the ring and the edge of its surface.
    ///
    /// <para>A node sized to exactly fill the surface has the outer half of its border stroke fall
    /// outside it, and a rounded node loses its widest points to the corners as well — so the ring
    /// arrived visibly flattened at top, bottom and sides. The margin has to exceed half the border
    /// width; it is comfortably more so the pulse has somewhere to sit.</para>
    /// </summary>
    private const float RadarMargin = 5f;

    /// <summary>
    /// Pulse rate at the edge of radar range and just before the spot.
    ///
    /// <para>Scaling frequency rather than size is what makes this read as a detector rather than a
    /// progress bar — the rhythm registers before the ring is consciously read.</para>
    ///
    /// <para><b>The top end is deliberately not fast.</b> It was 7 Hz, which is both unpleasant to
    /// stand next to and inside the band where flashing imagery is a genuine hazard for
    /// photosensitive players. 4 Hz still reads as clearly urgent, and the last stretch resolves to
    /// a steady fill anyway.</para>
    /// </summary>
    private const float MinPulseHz = 1.0f;
    private const float MaxPulseHz = 4.0f;

    private static Node BuildRadar(float closeness, float phase, float fade)
    {
        bool solid = closeness >= 1f;

        float wave = 0.5f + 0.5f * MathF.Sin(phase);

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
        // primitive and does not need one. Inset from the surface edge so the stroke fits; see
        // RadarMargin.
        const float Dial = RadarSize - RadarMargin * 2f;

        root.AppendChild(new Node().WithStyle(s =>
        {
            s.Position        = PositionMode.Absolute;
            s.Left            = RadarMargin;
            s.Top             = RadarMargin;
            s.WidthMode       = SizeMode.Fixed; s.Width  = Dial;
            s.HeightMode      = SizeMode.Fixed; s.Height = Dial;
            s.BorderRadius    = Dial / 2f;
            s.BackgroundColor = Ink.WithOpacity(0.55f * fade);
            s.BorderColor     = accent.WithOpacity(0.90f * fade);
            s.BorderWidth     = 2;
            s.PointerEvents   = PointerEvents.None;
        }));

        // The pulsing core, inset again so the outer ring always stays legible as a boundary.
        const float Inset = RadarMargin + 11f;
        const float Core  = RadarSize - Inset * 2f;

        root.AppendChild(new Node().WithStyle(s =>
        {
            s.Position        = PositionMode.Absolute;
            s.Left            = Inset;
            s.Top             = Inset;
            s.WidthMode       = SizeMode.Fixed; s.Width  = Core;
            s.HeightMode      = SizeMode.Fixed; s.Height = Core;
            s.BorderRadius    = Core / 2f;
            s.BackgroundColor = accent.WithOpacity(fill * fade);
            s.PointerEvents   = PointerEvents.None;
        }));

        return root;
    }

    // ── shared host-window plumbing ──────────────────────────────────────────

    /// <summary>
    /// Opens a borderless HUD window sized exactly to its image.
    ///
    /// <para><b>Window padding is forced to zero.</b> An ImGui window's content region is its size
    /// minus padding, so a window sized to the image clips the image by the padding on the right and
    /// bottom — invisible on a wide banner, obvious on a small square dial. Paired with
    /// <see cref="EndHud"/>, which pops it; every exit path has to go through one or the other or
    /// the style stack unbalances for every window drawn afterwards.</para>
    /// </summary>
    private static bool BeginHud(string id, Vector2 pos, int w, int h)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
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

        EndHud();
        return false;
    }

    private static void EndHud()
    {
        ImGui.End();
        ImGui.PopStyleVar();
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
