#if DEV_BUILD
using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

using PanacheUI.Components;
using PanacheUI.Core;
using PanacheUI.Rendering;

using SkiaSharp;

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
    /// <summary>
    /// The clue reminder's box. Wide, because a written clue is a sentence, and only as tall as
    /// three wrapped lines — it is text on the world, not a panel.
    /// </summary>
    private const int ReminderW = 560;
    private const int ReminderH = 88;

    /// <summary>
    /// Gap between the bottom of the dial and the top of the clue text, in logical pixels, plus the
    /// downward nudge applied to the pair as a whole. <b>Both are live tuning values</b> — see
    /// <see cref="DigTuning.HudClueGapPx"/> for why they cannot be constants: the game prints its
    /// own location banner into that gap, and where it lands is not something this plugin can read.
    ///
    /// <para><b>The gap only means what it says because the text layers are Fit-height.</b> They
    /// were fixed at the full surface height, which let the framework centre the text vertically
    /// inside an 88px box — so most of the visible gap was that centring, not this gap, and changing
    /// it had a fraction of the effect it appeared to. Top-aligned, the distance on screen is the
    /// distance set here.</para>
    /// </summary>
    private static float ReminderGapPx => DigTuning.HudClueGapPx;

    /// <summary>
    /// Downward offset applied to the dial AND the clue together, so the pair moves without the gap
    /// between them changing.
    /// </summary>
    private static float DropPx => DigTuning.HudDropPx;

    /// <summary>
    /// Reminder timing: fade in, hold, fade out. The total deliberately outlasts a dig, so a dig
    /// that reveals the NEXT clue shows that clue instead of fading out just before it arrives.
    /// </summary>
    private const float ReminderIn    = 0.35f;
    private const float ReminderOut   = 1.0f;
    private const float ReminderTotal = 8.0f;

    /// <summary>When the reminder was triggered. 0 = not showing.</summary>
    private long _reminderAt;

    private PanacheSurface? _reminder;

    /// <summary>
    /// Radar surface, drawn just under the banner. Square, so the ring is a circle.
    ///
    /// <para>128, not 96. The artwork is line art reduced from 1254px, so every doubling of the
    /// drawn size is the difference between a stroke surviving and a stroke falling below a pixel.
    /// This is the cheapest legibility gain available and the only one that adds no ink.</para>
    /// </summary>
    private const int RadarSize = 128;

    /// <summary>
    /// How much of the dial's width the dark backing disc covers, matching the ring art's inner
    /// circle. Behind the figure, inside the rim.
    ///
    /// <para><b>This is the change that actually makes the dial readable.</b> The icon was hard to
    /// see because it was thin strokes over whatever the world happened to be — pale flagstones one
    /// moment, grass the next — and no tint fixes that, because the problem is the background, not
    /// the figure. A dark disc gives the artwork a guaranteed ground to sit on, which is why every
    /// game HUD icon has one.</para>
    /// </summary>
    private const float BackingFraction = 0.68f;

    /// <summary>
    /// Outline and shadow offsets, in logical pixels before UI scale.
    ///
    /// <para>Both are drawn by re-stamping the same silhouette in near-black behind itself — eight
    /// directions for a rim, one larger down-right pass for depth. No second set of artwork is
    /// needed, and the outline tracks the figure exactly because it IS the figure.</para>
    /// </summary>
    private const float OutlinePx = 1.5f;
    private const float ShadowPx  = 3f;

    /// <summary>
    /// The shine that appears around the rim once a dig would land: how far it reaches past the
    /// artwork in logical pixels, how many nested stamps build it, and how slowly it breathes.
    ///
    /// <para><b>It is the same silhouette, stamped outward at falling alpha</b> — three rings of
    /// sixteen offsets each. There is no blur available on a draw list, and none is needed: nested
    /// copies of the shape at decreasing opacity is exactly what a bloom is, and because the halo IS
    /// the ring artwork it follows every notch of it rather than being a circle drawn near it.</para>
    ///
    /// <para>Sixteen directions rather than the outline's eight. An outline sits tight against the
    /// edge where eight is plenty; a halo reaching seven pixels out would show its corners.</para>
    /// </summary>
    private const float GlowPx    = 7f;
    private const int   GlowRings = 3;
    private const int   GlowSteps = 16;
    private const float ShineHz   = 0.9f;

    /// <summary>
    /// Distance down from the top of the viewport, as a fraction of its height. Low enough to clear
    /// the default target bar, high enough to stay out of the way of a fight.
    /// </summary>
    private const float TopFraction = 0.16f;

    private static readonly PColor Ink = PColor.FromHex("#12101A");

    // Bright and saturated on purpose: these tint line art drawn over the open world, where the
    // removed banner's equivalents had their own dark panel to sit on.
    private static readonly PColor Green  = PColor.FromHex("#8CF5B4");
    private static readonly PColor DigCol = PColor.FromHex("#FFD84D");

    /// <summary>
    /// The far end of the proximity ramp.
    ///
    /// <para><b>Blue, not grey.</b> Grey is what a HUD element looks like when it is disabled, so
    /// the coldest end of a live readout was reading as "not working" rather than as "a long way
    /// off". A saturated blue is unmistakably switched-on while still being as far from the warm end
    /// as a hue can get — and blue → green → yellow is the temperature order everything else in this
    /// plugin already uses, so the dial now agrees with the bands.</para>
    /// </summary>
    private static readonly PColor DeepBlue = PColor.FromHex("#2F5BE8");

    /// <summary>
    /// Where the ramp stops travelling blue → green and starts travelling green → yellow.
    ///
    /// <para>Deliberately late. Green has to be a place the dial visibly ARRIVES at and sits in for
    /// a moment, or it is just a colour the gradient passes through on its way to yellow and carries
    /// no meaning. The last quarter is the "very close" stretch, and yellow is reserved for it.</para>
    /// </summary>
    private const float WarmSplit = 0.75f;

    /// <summary>How long the click press lasts. Long enough to see, short enough to feel instant.</summary>
    private const long PressMs = 150;

    /// <summary>How far the dial sinks when pressed, in logical pixels, down AND right.</summary>
    private const float PressOffsetPx = 2.5f;

    /// <summary>When the dial was last clicked. 0 = never.</summary>
    private long _pressedAtMs;

    private readonly ITextureProvider _texProvider;
    private readonly Action?          _onDig;
    private readonly DateTime         _start = DateTime.UtcNow;

    /// <summary>
    /// Whether the dial was under the pointer last frame. One frame stale by construction — the
    /// tree has to be built before ImGui can be asked what the mouse is over — which is invisible
    /// at any frame rate and is the same trade every hover cue in this plugin makes.
    /// </summary>
    private bool _radarHovered;

    /// <summary>
    /// The dig dial's artwork: a steady outer ring and a pulsing centre.
    ///
    /// <para>Both are white-on-transparent silhouettes, so a tint multiplies cleanly and the same
    /// pair serves every band colour. Resolved once and cached — <c>GetFromFile</c> is cheap on
    /// repeat but this runs every frame, and a null result must not turn into a per-frame disk
    /// probe.</para>
    ///
    /// <para><b>Absent artwork is a supported state, not a failure.</b> The PNGs are copied for the
    /// Debug build only, so the public build genuinely has none — and the drawn circles this
    /// replaces are still there as the fallback. Nothing about the dial's behaviour depends on
    /// which of the two is on screen.</para>
    /// </summary>
    /// <summary>
    /// The dial textures, built by this class at the exact size they are drawn — see
    /// <see cref="EnsureDialArt"/>. Owned here, and therefore disposed here.
    ///
    /// <para>These replaced shared textures loaded straight from file. That route handed the
    /// reduction to the GPU sampler, which is what made the artwork look low-resolution; it also
    /// returned null on the frame a file was first requested, which is a trap if the result is
    /// cached behind a one-shot latch. Building them here avoids both.</para>
    /// </summary>
    private IDalamudTextureWrap? _dialOuter;
    private IDalamudTextureWrap? _dialCentre;

    /// <summary>Device-pixel size the current pair was built for. 0 = nothing built yet.</summary>
    private int _dialBuiltFor;

    private const string CentreFile = "NatalMSQDigIcon_centerIcon.png";
    private const string OuterFile  = "NatalMSQDigIcon_outerCircle.png";

    /// <summary>
    /// The word arcing over the dial. <b>Drawn at exactly the same rect as the dial</b>, because
    /// they were authored on the same 1254px canvas — the lettering occupies its top ~12–39%, so
    /// drawing it anywhere else would be second-guessing where the artist already put it. Measured,
    /// not assumed.
    /// </summary>
    private const string ClueFile = "CLUE!_digIcon.png";
    private const string DigFile  = "DIG!_digIcon.png";

    private IDalamudTextureWrap? _labelClue;
    private IDalamudTextureWrap? _labelDig;

    /// <summary>
    /// How far the word is lifted above its authored position, in logical pixels.
    ///
    /// <para>The artwork is drawn at the dial's own rect, which is where it was composed — this is a
    /// deliberate nudge off that, so the lettering clears the rim rather than sitting into it. Kept
    /// as its own number rather than folded into the draw so it stays obvious that the label is
    /// offset from the canvas on purpose.</para>
    /// </summary>
    private const float LabelLiftPx = 26f;

    /// <summary>
    /// Empty space reserved ABOVE the dial inside its window, so the lifted label has somewhere to
    /// be drawn.
    ///
    /// <para><b>Without this the lift is a clip, not a move.</b> The window was exactly the dial's
    /// size, so every pixel the label rose above the dial's own rect fell outside the window and
    /// ImGui cut it off — raising the lift further only removed more of the word. The window is now
    /// taller by this much and shifted up by the same, so the dial stays exactly where it was on
    /// screen and the space appears above it rather than pushing anything down.</para>
    ///
    /// <para>Must exceed <see cref="LabelLiftPx"/> plus the outline and shadow reach, or the top of
    /// the lettering clips again.</para>
    /// </summary>
    private const float LabelHeadroomPx = 38f;

    /// <summary>
    /// Builds the dial textures at <b>exactly</b> the size they will be drawn, by reducing the
    /// source in steps rather than letting the GPU sampler do it.
    ///
    /// <para><b>This is where the "low resolution" look came from, and it was not a shortage of
    /// source detail.</b> The artwork is 1254px drawn at ~128, so about ninety-six source pixels
    /// fall under each output pixel — and a bilinear tap reads four of them. Nearly all the detail
    /// supplied was being skipped, and which four got picked shifted with sub-pixel position, so
    /// thin strokes thinned, dropped out, or doubled. More source resolution cannot help a sampler
    /// that ignores it.</para>
    ///
    /// <para><b>Halving averages an exact 2×2 block, so every source pixel contributes.</b> Repeat
    /// until within 2× of the target, then one high-quality resample covers the short hop. That is
    /// what mipmapping does internally, done explicitly here because nothing in this path builds a
    /// mip chain — the same reasoning, and the same fix, as PanacheUI's own icon scaling.</para>
    ///
    /// <para>Rebuilt when the UI scale changes the target size, so a larger scale genuinely
    /// resolves more detail instead of magnifying the small copy.</para>
    /// </summary>
    private void EnsureDialArt(int targetPx)
    {
        if (targetPx <= 0 || targetPx == _dialBuiltFor) return;

        _dialBuiltFor = targetPx;

        try
        {
            string? dir = Plugin.PluginInterface.AssemblyLocation.Directory?.FullName;
            if (string.IsNullOrEmpty(dir)) return;

            string folder = System.IO.Path.Combine(dir!, "digicons");
            string centre = System.IO.Path.Combine(folder, CentreFile);
            string outer  = System.IO.Path.Combine(folder, OuterFile);

            if (!System.IO.File.Exists(centre) || !System.IO.File.Exists(outer))
            {
                Diag.Info($"[Dig] dial artwork not in '{folder}' — using the drawn dial.");
                return;
            }

            var newCentre = BuildScaled(centre, targetPx);
            var newOuter  = BuildScaled(outer,  targetPx);

            if (newCentre == null || newOuter == null)
            {
                newCentre?.Dispose();
                newOuter?.Dispose();
                return;
            }

            // Swapped in only once BOTH succeeded, so a half-built pair never reaches the draw.
            _dialCentre?.Dispose();
            _dialOuter?.Dispose();
            _dialCentre = newCentre;
            _dialOuter  = newOuter;

            // Labels are optional: a missing one costs the word, not the dial.
            var newClue = BuildScaled(System.IO.Path.Combine(folder, ClueFile), targetPx);
            var newDig  = BuildScaled(System.IO.Path.Combine(folder, DigFile),  targetPx);

            if (newClue != null) { _labelClue?.Dispose(); _labelClue = newClue; }
            if (newDig  != null) { _labelDig?.Dispose();  _labelDig  = newDig;  }

            Diag.Info($"[Dig] dial artwork resampled to {targetPx}px.");
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] dial artwork failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Decodes a PNG and reduces it to <paramref name="target"/> square by progressive halving plus
    /// one final resample. Returns null on any failure — the drawn dial is always a valid fallback.
    /// </summary>
    private IDalamudTextureWrap? BuildScaled(string path, int target)
    {
        SKBitmap? working = null;

        try
        {
            working = SKBitmap.Decode(path);
            if (working == null) return null;

            // Halve while a halved copy would still be at least the target. Each step is an exact
            // 2×2 average, which is the only way every source pixel gets a vote.
            while (working.Width / 2 >= target && working.Width > 2)
            {
                // LINEAR for the halving steps, deliberately: at exactly 2:1 a linear filter
                // averages the 2×2 block and nothing else, which is precisely the "every pixel
                // votes" property this loop exists for. A cubic here would reach outside the block
                // and soften it for no gain.
                var half = working.Resize(
                    new SKImageInfo(working.Width / 2, working.Height / 2,
                                    SKColorType.Bgra8888, SKAlphaType.Unpremul),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));

                if (half == null) break;

                working.Dispose();
                working = half;
            }

            // Mitchell for the final, non-power-of-two hop — the standard choice for downscaling
            // artwork, sharper than linear without the ringing a sharper cubic puts on edges.
            var final = working.Resize(
                new SKImageInfo(target, target, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                new SKSamplingOptions(SKCubicResampler.Mitchell));

            if (final == null) return null;

            try
            {
                return _texProvider.CreateFromRaw(
                    RawImageSpecification.Bgra32(target, target),
                    final.Bytes,
                    $"TieriChallenges.Dial.{System.IO.Path.GetFileNameWithoutExtension(path)}");
            }
            finally { final.Dispose(); }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] resample of '{System.IO.Path.GetFileName(path)}' failed: {ex.Message}");
            return null;
        }
        finally { working?.Dispose(); }
    }

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
    /// Phase of the "you are standing on it" shine around the rim. Its own accumulator, and
    /// integrated for the same reason <see cref="_radarPhase"/> is.
    ///
    /// <para><b>It must not share the pulse's phase or its rate.</b> The pulse is at its fastest
    /// exactly when the shine appears — <see cref="MaxPulseHz"/>, four beats a second — and a halo
    /// breathing at that rate is a strobe, not a glow. The shine runs at its own slow rate so the
    /// dial reads as steady-and-lit rather than as flashing harder than it did a moment ago.</para>
    /// </summary>
    private float _shinePhase;

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

    /// <param name="onDig">
    /// Runs when the dial is clicked. Supplied by the plugin rather than reached for here, so this
    /// class stays presentation — it draws what a test reports and forwards a click, and does not
    /// itself decide that a click means "dig".
    /// </param>
    public DigHuntOverlay(ITextureProvider texProvider, Action? onDig = null)
    {
        _texProvider = texProvider;
        _onDig       = onDig;
    }

    public void Dispose()
    {
        _reminder?.Dispose(); _reminder = null;
        _radar?.Dispose();    _radar    = null;

        _labelClue?.Dispose(); _labelClue = null;
        _labelDig?.Dispose();  _labelDig  = null;

        // These ARE ours — built by CreateFromRaw rather than borrowed from the shared cache.
        _dialOuter?.Dispose();  _dialOuter  = null;
        _dialCentre?.Dispose(); _dialCentre = null;
    }

    public void Draw(DigTests tests)
    {
        var active = tests.Active;

        if (active == null)
        {
            // Nothing running: the next test starts from silence rather than inheriting a
            // half-faded ring from the last one.
            _radarFade   = 0f;
            _radarPhase  = 0f;
            _shinePhase  = 0f;
            _reminderAt  = 0;
            _hiddenForDig = false;
            return;
        }

        // The reminder is drawn whether or not the dial is — it exists precisely to be readable
        // while the dial is away for the dig.
        DrawReminder(active);

        // A dig started from the right spot takes the button away for its duration. Cleared the
        // moment the performance ends, so being back in range simply brings it back.
        if (_hiddenForDig && !Plugin.Props.IsPerforming)
        {
            _hiddenForDig = false;

            // …unless the dig that took it away also ENDED the thing it was pointing at. Then it
            // does not come back at all.
            //
            // This is what put a flash on the end of a trail. The fade envelope was still pinned at
            // full — the player was standing on the spot for the whole dig — so releasing the button
            // handed a fully-opaque dial to a ramp that then had to walk it down to nothing. The
            // dial reappeared for the length of RadarFadeSeconds after the trail was already over,
            // reading as a last blink rather than as a fade-out, because the thing it faded out FROM
            // was never on screen: it had been hidden the whole time it was at full.
            //
            // Snapped rather than ramped, and snapped HERE rather than by shortening the ramp: a
            // fade is for leaving a range, and this is not that. There is nothing left to point at.
            //
            // Safe to read RadarCloseness now — Plugin.DrawUI ticks the tests before it draws this,
            // so a dig that finished this frame has already been banked by DigTests.Tick.
            if (!active.RadarCloseness.HasValue)
            {
                _radarFade  = 0f;
                _radarPhase = 0f;
                _shinePhase = 0f;
            }
        }

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

        // Hidden for the dig, but only once the press has finished playing: the click has to be
        // seen to land before the button is allowed to disappear, or it reads as the dial
        // vanishing on contact rather than as a button being pressed.
        if (_hiddenForDig && Environment.TickCount64 - _pressedAtMs >= PressMs) return;

        DrawRadar(_radarCloseness, _radarFade);
    }

    /// <summary>True while the dial is withheld for a dig started from the right spot.</summary>
    private bool _hiddenForDig;

    // ── the clue reminder ────────────────────────────────────────────────────

    /// <summary>
    /// The clue, in plain white type under the dial, with a drop shadow and no panel of any kind.
    ///
    /// <para>This replaced a full banner. A permanent panel meant the clue was always on screen and
    /// therefore never read — and it occupied the top of the view for the whole run. Showing it only
    /// when the player digs turns it from decoration into an answer to a question they just asked.
    /// </para>
    ///
    /// <para>Held long enough to outlast the dig itself, so a dig that reveals the NEXT clue shows
    /// that clue rather than fading out just before it arrives.</para>
    ///
    /// <para>Drawn with two text nodes rather than one: the dark copy offset behind the white one is
    /// the shadow. Without it white type is unreadable against snow, sand or a lit wall — the same
    /// contrast problem the dial's backing disc solves, solved the way text solves it.</para>
    /// </summary>
    private void DrawReminder(IDigTest test)
    {
        if (_reminderAt == 0) return;

        float elapsed = (Environment.TickCount64 - _reminderAt) / 1000f;
        float alpha   = ReminderAlpha(elapsed);

        if (alpha <= 0.002f) { _reminderAt = 0; return; }

        string text = test.Subtitle;
        if (string.IsNullOrWhiteSpace(text)) return;

        float uiScale = UiScale.Factor;
        int   physW   = (int)(ReminderW * uiScale);
        int   physH   = (int)(ReminderH * uiScale);

        var viewport = ImGui.GetMainViewport();
        var pos = new Vector2(
            viewport.Pos.X + (viewport.Size.X - physW) * 0.5f,
            viewport.Pos.Y + viewport.Size.Y * TopFraction
                           + (DropPx + RadarSize + ReminderGapPx) * uiScale);

        if (!BeginHud("##tc_dig_reminder", pos, physW, physH)) return;

        _reminder ??= new PanacheSurface(_texProvider, physW, physH);
        _reminder.Resize(physW, physH);
        _reminder.Scale = uiScale;

        float time = (float)(DateTime.UtcNow - _start).TotalSeconds;

        var (tex, _) = _reminder.Render(BuildReminder(text, alpha), time, Vector2.Zero, false,
                                        false, 0f, ImGui.GetIO().DeltaTime, forceRedraw: false);

        if (tex.HasValue) ImGui.Image(tex.Value, new Vector2(physW, physH));
        EndHud();
    }

    /// <summary>Fade in, hold, fade out. Sums to <see cref="ReminderTotal"/>.</summary>
    private static float ReminderAlpha(float elapsed)
    {
        if (elapsed < 0f)             return 0f;
        if (elapsed < ReminderIn)     return elapsed / ReminderIn;
        if (elapsed < ReminderTotal - ReminderOut) return 1f;
        if (elapsed < ReminderTotal)  return (ReminderTotal - elapsed) / ReminderOut;
        return 0f;
    }

    private static Node BuildReminder(string text, float alpha)
    {
        var root = new Node().WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fixed; s.Width  = ReminderW;
            s.HeightMode = SizeMode.Fixed; s.Height = ReminderH;
        });

        // Shadow first, then the face directly over it. Two absolutely-positioned copies of the
        // same string, so they cannot wrap differently and split.
        root.AppendChild(TextLayer(text, new Vector2(2f, 2f), PColor.Black.WithOpacity(0.75f * alpha)));
        root.AppendChild(TextLayer(text, Vector2.Zero,        PColor.White.WithOpacity(alpha)));

        return root;
    }

    private static Node TextLayer(string text, Vector2 offset, PColor colour)
        => new Node().WithText(text).WithStyle(s =>
        {
            s.Position     = PositionMode.Absolute;
            s.Left         = offset.X;
            s.Top          = offset.Y;
            s.WidthMode    = SizeMode.Fixed; s.Width  = ReminderW;

            // Fit, NOT the surface height. A fixed-height text node lets the framework centre the
            // line vertically inside it, which put the text far lower than ReminderGapPx claimed
            // and made that constant nearly inert. Fit puts the first line at the top, so the gap
            // above is the only thing deciding where the text sits.
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 21f;
            s.Bold         = true;
            s.Color        = colour;
            s.TextAlign    = TextAlign.Center;
            s.TextOverflow = TextOverflow.Wrap;
            s.MaxLines     = 3;
            s.PointerEvents = PointerEvents.None;
        });


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

        _shinePhase += ImGui.GetIO().DeltaTime * MathF.Tau * ShineHz;
        if (_shinePhase > MathF.Tau) _shinePhase %= MathF.Tau;

        float uiScale = UiScale.Factor;
        int   phys    = (int)(RadarSize * uiScale);

        int headroom = (int)MathF.Round(LabelHeadroomPx * uiScale);

        var viewport = ImGui.GetMainViewport();

        // The window starts ABOVE the dial by exactly the headroom and is that much taller, so the
        // dial itself still lands on TopFraction. The reminder below adds the SAME drop, so the two
        // move as one and the gap between them is decided only by ReminderGapPx.
        var pos = new Vector2(
            viewport.Pos.X + (viewport.Size.X - phys) * 0.5f,
            viewport.Pos.Y + viewport.Size.Y * TopFraction + DropPx * uiScale - headroom);

        // The one HUD window that takes input — it is a button. Kept small and only present while
        // the dial is visible, so the amount of screen that can swallow a click is one small circle
        // for the few seconds it is up.
        if (!BeginHud("##tc_dig_radar", pos, phys, phys + headroom, acceptInput: true)) return;

        // Step past the reserved space, so everything below draws from the dial's top-left exactly
        // as it did before the headroom existed — including the hit rect.
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + headroom);

        EnsureDialArt(phys);

        bool solid = closeness >= 1f;

        // Deep blue at the edge of radar range, green as it closes, yellow in the last stretch. The
        // pulse says "something is here"; the colour says how near, so the two carry different
        // information instead of both restating proximity.
        Vector3 baseRgb = solid ? Rgb(DigCol) : Ramp(closeness);

        // Click feedback. Instant depress, then an ease back over PressMs — a linear decay reads as
        // a press-and-release, where a symmetric in-out reads as a pulse and gets confused with the
        // one already running.
        float press = 0f;
        if (_pressedAtMs > 0)
        {
            long since = Environment.TickCount64 - _pressedAtMs;
            press = since >= PressMs ? 0f : 1f - since / (float)PressMs;
        }

        // Darker and less saturated while held, so the dial reads as taking the click rather than
        // merely moving.
        var accentRgb = Vector3.Lerp(baseRgb, Desaturate(baseRgb) * 0.62f, press);

        float wave = 0.5f + 0.5f * MathF.Sin(_radarPhase);

        // The trough is 0.55, not 0.18. A pulse that fades almost to nothing spends half its cycle
        // illegible, which reads as a flicker rather than a heartbeat — and legibility was the
        // complaint. The swing is still obvious against a dark backing; it just never disappears.
        float fill = solid ? 1f : 0.55f + 0.45f * wave;

        var dialOuter  = _dialOuter;
        var dialCentre = _dialCentre;

        if (dialOuter != null && dialCentre != null)
        {
            // Artwork path. The ring holds steady and only the centre pulses — the border is the
            // thing that says "a dial is here", and a border that blinks out takes the dial with it.
            //
            // Snapped to whole device pixels: the textures are built at exactly this size, so a
            // fractional destination would have the GPU resample a 1:1 blit across a half-pixel
            // offset and give back the sharpness the resampling just bought.
            var raw      = ImGui.GetCursorScreenPos();
            var anchor   = new Vector2(MathF.Round(raw.X), MathF.Round(raw.Y));
            var size     = new Vector2(phys, phys);
            var drawList = ImGui.GetWindowDrawList();

            // The artwork sinks; the HIT AREA does not. A button whose target moves out from under
            // the cursor on press is a button that eats double-clicks.
            float sink   = press * PressOffsetPx * uiScale;
            var   origin = anchor + new Vector2(sink, sink);

            float ringAlpha = (_radarHovered ? 1f : 0.95f) * fade;
            float outline   = OutlinePx * uiScale;

            // The shadow tightens as the dial sinks toward the surface — the same cue a real
            // button gives, and the reason the press reads as depth rather than as a slide.
            float shadow = ShadowPx * uiScale * (1f - 0.7f * press);

            // Order is the whole effect: shadow, ground, then rim and figure each over their own
            // outline. Anything drawn out of this order loses its separation.

            // 1. Depth. One offset stamp of both shapes, dark and soft, so the dial sits ON the
            //    world rather than being printed flat against it.
            uint shadowCol = Tint(Ink, 0.45f * fade);
            drawList.AddImage(dialOuter.Handle,  origin + new Vector2(shadow, shadow),
                              origin + size + new Vector2(shadow, shadow),
                              Vector2.Zero, Vector2.One, shadowCol);
            drawList.AddImage(dialCentre.Handle, origin + new Vector2(shadow, shadow),
                              origin + size + new Vector2(shadow, shadow),
                              Vector2.Zero, Vector2.One, shadowCol);

            // 2. The backing disc — the reason the figure is legible over anything at all.
            var  centreScreen = origin + size * 0.5f;
            float backingR    = phys * 0.5f * BackingFraction;
            drawList.AddCircleFilled(centreScreen, backingR, Tint(Ink, 0.72f * fade), 48);

            // 2b. The shine. ONLY when a dig would land — it is the reward for arriving, and a glow
            //     that were merely the brightest step of the ramp would say nothing the colour was
            //     not already saying. Drawn under the ink outline, so it spreads outward from the
            //     rim rather than washing over it.
            if (solid)
            {
                float shine = 0.55f + 0.45f * (0.5f + 0.5f * MathF.Sin(_shinePhase));

                for (int ring = 1; ring <= GlowRings; ring++)
                {
                    float reach = outline + GlowPx * uiScale * ring / GlowRings;

                    Stamp(drawList, dialOuter, origin, size, reach,
                          Tint(accentRgb, 0.17f * shine * fade), GlowSteps);
                }
            }

            // 3. Rim, outlined.
            Stamp(drawList, dialOuter, origin, size, outline, Tint(Ink, 0.85f * fade));
            drawList.AddImage(dialOuter.Handle, origin, origin + size,
                              Vector2.Zero, Vector2.One, Tint(accentRgb, ringAlpha));

            // 4. Figure, outlined. Its alpha carries the pulse, so the outline pulses with it —
            //    an outline holding steady while its fill breathes reads as two objects.
            float centreAlpha = fill * fade;
            Stamp(drawList, dialCentre, origin, size, outline, Tint(Ink, 0.85f * centreAlpha));
            drawList.AddImage(dialCentre.Handle, origin, origin + size,
                              Vector2.Zero, Vector2.One, Tint(accentRgb, centreAlpha));

            // 5. The word, over the top. Same rect as the dial — the lettering was authored into
            //    the top of the same canvas, so this is where it curves over the rim by design.
            //    DIG when a dig would land, CLUE while still looking.
            var label = solid ? _labelDig : _labelClue;
            if (label != null)
            {
                // Lifted clear of the rim. Rides the press with everything else, so the word sinks
                // with the dial instead of hovering while the button moves under it.
                var labelOrigin = origin - new Vector2(0f, LabelLiftPx * uiScale);

                Stamp(drawList, label, labelOrigin, size, outline, Tint(Ink, 0.85f * fade));
                drawList.AddImage(label.Handle, labelOrigin, labelOrigin + size,
                                  Vector2.Zero, Vector2.One, Tint(accentRgb, ringAlpha));
            }

            // Claims the same rectangle for hit-testing, since AddImage draws without laying
            // anything out.
            ImGui.Dummy(size);
        }
        else
        {
            _radar ??= new PanacheSurface(_texProvider, phys, phys);
            _radar.Resize(phys, phys);
            _radar.Scale = uiScale;

            float time = (float)(DateTime.UtcNow - _start).TotalSeconds;

            var (tex, _) = _radar.Render(BuildRadar(closeness, _radarPhase, fade, _radarHovered),
                                         time, Vector2.Zero, false, false, 0f,
                                         ImGui.GetIO().DeltaTime, forceRedraw: false);

            if (tex.HasValue) ImGui.Image(tex.Value, new Vector2(phys, phys));
        }

        // Hit-test the CIRCLE, not the square it is drawn in. The corners are transparent, and a
        // click that lands on nothing visible but still counts is the kind of thing that feels
        // broken — and here it would also be stealing a click from the game for no reason.
        _radarHovered = false;

        if (ImGui.IsItemHovered())
        {
            var min    = ImGui.GetItemRectMin();
            var centre = new Vector2(min.X + phys * 0.5f, min.Y + phys * 0.5f);
            float r    = phys * 0.5f;

            _radarHovered = Vector2.DistanceSquared(ImGui.GetMousePos(), centre) <= r * r;
        }

        if (_radarHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _pressedAtMs = Environment.TickCount64;

            // Only a dig from the RIGHT spot takes the button away. A miss leaves it up, because
            // the player still needs it — hiding it would punish the wrong guess twice.
            bool onSpot = closeness >= 1f;
            _hiddenForDig = onSpot;

            // The clue goes with it, and for the same reason. A reminder of where to dig is only
            // worth reading while the answer is still wanted; once the right hole is being dug the
            // question is settled, and leaving the old clue on screen would contradict the button
            // that just conceded the point. A MISS keeps it — that is exactly when it is wanted.
            //
            // Cleared outright rather than faded, so it leaves with the button instead of trailing
            // a second behind it.
            _reminderAt = onSpot ? 0 : _pressedAtMs;

            _onDig?.Invoke();
        }

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

    private static Node BuildRadar(float closeness, float phase, float fade, bool hovered)
    {
        bool solid = closeness >= 1f;

        float wave = 0.5f + 0.5f * MathF.Sin(phase);

        // Never fully transparent even at the outer edge: a ring that vanishes between beats reads
        // as a bug rather than as a slow pulse.
        float fill = solid ? 1f : 0.18f + 0.62f * wave;

        var accent = solid ? DigCol : RampColor(closeness);

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

            // The one cue that it is clickable. A ring that is already pulsing cannot say "hover"
            // by changing brightness alone, so hover thickens the rim as well.
            s.BorderColor     = accent.WithOpacity((hovered ? 1f : 0.90f) * fade);
            s.BorderWidth     = hovered ? 3 : 2;
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
    private static bool BeginHud(string id, Vector2 pos, int w, int h, bool acceptInput = false)
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
                  | ImGuiWindowFlags.NoNav;

        // NoInputs by default — a HUD that eats clicks aimed at the game is hostile. The dial opts
        // out because it IS a button; nothing else should.
        if (!acceptInput) flags |= ImGuiWindowFlags.NoInputs;

        if (ImGui.Begin(id, flags)) return true;

        EndHud();
        return false;
    }

    private static void EndHud()
    {
        ImGui.End();
        ImGui.PopStyleVar();
    }

    /// <summary>
    /// Re-stamps a silhouette eight ways around its own position, producing an outline that follows
    /// the artwork exactly — because it is the artwork.
    ///
    /// <para>Eight directions rather than four: at four, diagonal strokes get an outline on their
    /// flat sides and none on their corners, which reads as a broken edge rather than a rim. The
    /// count is a parameter because the shine reaches much further out, where eight would show as
    /// an octagon.</para>
    /// </summary>
    private static void Stamp(ImDrawListPtr drawList, IDalamudTextureWrap tex,
                              Vector2 origin, Vector2 size, float offset, uint colour,
                              int steps = 8)
    {
        if (offset <= 0f || steps <= 0) return;

        for (int i = 0; i < steps; i++)
        {
            float a = MathF.Tau * i / steps;
            var   d = new Vector2(MathF.Cos(a) * offset, MathF.Sin(a) * offset);

            drawList.AddImage(tex.Handle, origin + d, origin + size + d,
                              Vector2.Zero, Vector2.One, colour);
        }
    }

    /// <summary>
    /// The proximity colour for a closeness in 0..1: deep blue → green over the first
    /// <see cref="WarmSplit"/>, green → the dig yellow over the rest.
    ///
    /// <para><b>The warm end is the dig colour itself, not a different yellow.</b> The ramp
    /// therefore ARRIVES at exactly the colour the solid state uses, so crossing into dig range
    /// changes the dial's behaviour — steady fill, and the shine — without changing its hue. A
    /// separate near-yellow would put a visible colour jump at the boundary and make the two states
    /// look like they disagree about where the spot is.</para>
    /// </summary>
    private static Vector3 Ramp(float closeness)
    {
        float t = Math.Clamp(closeness, 0f, 1f);

        return t <= WarmSplit
            ? Vector3.Lerp(Rgb(DeepBlue), Rgb(Green),  t / WarmSplit)
            : Vector3.Lerp(Rgb(Green),    Rgb(DigCol), (t - WarmSplit) / (1f - WarmSplit));
    }

    private static PColor RampColor(float closeness)
    {
        var rgb = Ramp(closeness);
        return new PColor((byte)MathF.Round(rgb.X * 255f),
                          (byte)MathF.Round(rgb.Y * 255f),
                          (byte)MathF.Round(rgb.Z * 255f));
    }

    /// <summary>A PanacheUI colour as an ImGui tint, with an extra opacity applied.</summary>
    private static uint Tint(PColor c, float alpha) => Tint(Rgb(c), alpha);

    private static uint Tint(Vector3 rgb, float alpha)
        => ImGui.GetColorU32(new Vector4(rgb.X, rgb.Y, rgb.Z, Math.Clamp(alpha, 0f, 1f)));

    private static Vector3 Rgb(PColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    /// <summary>
    /// Pulls a colour halfway to its own brightness. Desaturating rather than just darkening is
    /// what makes the press read as "pushed in" instead of "shaded" — a pressed control loses
    /// vividness, not only light.
    /// </summary>
    private static Vector3 Desaturate(Vector3 rgb)
    {
        float lum = rgb.X * 0.299f + rgb.Y * 0.587f + rgb.Z * 0.114f;
        return Vector3.Lerp(rgb, new Vector3(lum), 0.5f);
    }
}
#endif
