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

    /// <summary>
    /// Tall enough for three wrapped lines at the current font size, plus room for the rim to reach
    /// above the first and the shadow below the last. Derived rather than fixed, because the font
    /// size is now a knob and a surface sized to yesterday's font clips today's.
    /// </summary>
    private static int ReminderH =>
        (int)MathF.Ceiling(DigTuning.ClueFontSize * 1.35f * 3f
                           + ClueTopInset + ClueShadowPx + ClueRimPx + 6f);

    /// <summary>
    /// The clue's three layers: a light-yellow face over a white rim over a dark drop shadow.
    ///
    /// <para><b>Plain white type does not survive snow, sand or a lit wall.</b> That is not a
    /// contrast problem a colour can fix on its own — whatever colour the face is, some ground in
    /// Eorzea is that colour. So the legibility comes from the SHADOW, which is dark against every
    /// bright surface, while the white rim separates the face from it and keeps the letters from
    /// reading as smudged. The face is then free to be a colour that belongs with the gold dial
    /// rather than one chosen to fight the background.</para>
    ///
    /// <para>The rim is deliberately drawn OUTSIDE the shadow's offset, so the shadow still shows
    /// past it down and right. A rim wider than the shadow's offset swallows it and the whole
    /// effect flattens to outlined text.</para>
    /// </summary>
    private static PColor ClueFace => FromVec(DigTuning.ClueFaceColor);
    private static PColor ClueRim  => FromVec(DigTuning.ClueOutlineColor);

    private static float ClueRimPx    => DigTuning.ClueOutline ? DigTuning.ClueOutlineWidth : 0f;
    private static float ClueShadowPx => DigTuning.ClueShadow  ? DigTuning.ClueShadowOffset : 0f;

    private const int ClueRimSteps = 8;

    /// <summary>
    /// Pushes every layer down inside the surface so the rim's upward reach has somewhere to go.
    /// Vertical only: shifting horizontally too would move the centred text off the box's centre.
    /// </summary>
    private const float ClueTopInset = 3f;

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
    /// Reminder timing: seconds to fade fully in, seconds to fade fully out, and how long it asks to
    /// stay up for after each trigger.
    /// </summary>
    private const float ReminderIn   = 0.35f;
    private const float ReminderOut  = 1.0f;
    private const float ReminderHold = 6.5f;

    /// <summary>
    /// Reminder opacity, 0 to 1. <b>A value that ramps, not an animation with a start.</b>
    ///
    /// <para>This is the same correction the radar ring already carries, made for the same reason
    /// and after the same bug. The reminder used to be "elapsed since triggered", run through a
    /// three-phase curve — which meant re-triggering while it was fading out reset elapsed to 0 and
    /// therefore snapped the opacity to 0 before climbing again. Digging as a clue faded produced a
    /// blink rather than the clue coming back.</para>
    ///
    /// <para>Ramping toward a target has no such seam: whatever the opacity currently is, it simply
    /// starts moving the other way. Re-triggering can only ever extend, never restart.</para>
    /// </summary>
    private float _reminderAlpha;

    /// <summary>Tick until which the reminder wants to be visible. Extended by every trigger.</summary>
    private long _reminderUntil;

    /// <summary>
    /// The line last shown, so a CHANGE of line can trigger a fresh showing.
    ///
    /// <para>This is what actually gets the next clue on screen. A dig takes seconds, and the new
    /// clue does not exist until it finishes — so the showing triggered by the CLICK is displaying
    /// the old line and is half spent by the time the new one arrives. Triggering on the text
    /// changing gives every new clue its own full duration, whenever it turns up.</para>
    /// </summary>
    private string _reminderText = string.Empty;

    /// <summary>
    /// Opacity of the DIG! word, ramped exactly like every other envelope here.
    ///
    /// <para><b>The two words do not share a rule, because they answer different questions.</b>
    /// CLUE! belongs to the clue: it is a label for the line underneath, so it borrows that line's
    /// opacity outright and appears and leaves with it — a heading with nothing under it is just
    /// clutter. DIG! belongs to the SPOT, which is a state the player is standing in rather than a
    /// message that was shown, so it needs an envelope of its own that follows being in range.</para>
    /// </summary>
    private float _digWordAlpha;

    /// <summary>
    /// A gate on the CLUE! word, not a fade of its own — it is still multiplied by the clue line's
    /// opacity, so CLUE! can never outlast the clue it labels.
    ///
    /// <para><b>It drops to zero INSTANTLY and returns on a ramp, and the asymmetry is the whole
    /// point.</b> The two words occupy the same place on screen, so any moment where both are
    /// partly visible is a double exposure — one word printed through the other. Crossfading them
    /// looked correct written down and is unreadable in practice. DIG! therefore takes the slot the
    /// instant it is wanted and CLUE! is simply gone that frame.</para>
    ///
    /// <para>Coming back is ramped because there is nothing to collide with: DIG! is already fully
    /// faded by then, so CLUE! is appearing over empty space and a hard cut would just look like a
    /// glitch.</para>
    /// </summary>
    private float _clueWordAlpha;

    private const float WordFadeIn  = 0.28f;
    private const float WordFadeOut = 0.45f;

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
    /// <summary>
    /// The outline width is a live tuning value now — see <see cref="DigTuning.IconOutlineWidth"/>.
    /// Width is the one thing that decides whether an outer outline reads as hugging the edge or as
    /// creeping into the art, so it belongs on a slider rather than in a constant.
    /// </summary>
    private static float OutlinePx => Math.Clamp(DigTuning.IconOutlineWidth, 0f, 6f);

    private const float ShadowPx = 3f;

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
    /// The dial when there is nothing to be near — out of radar range entirely.
    ///
    /// <para>Grey is correct HERE and was wrong as the cold end of the proximity ramp, and the
    /// difference is what the two states mean. A cold reading is a live detector saying "a long way
    /// off", which grey reads as broken. This is the detector saying nothing at all: no signal, no
    /// distance, just a button to press for the clue again. Grey is exactly right for that.</para>
    /// </summary>
    private static readonly PColor IdleGrey = PColor.FromHex("#A8ADB5");

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
    private readonly Action?          _onRecall;
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
    /// The word's artwork is built at the size the WORD is drawn, which is the dial's size times
    /// <see cref="DigTuning.LabelScale"/> — not the dial's size.
    ///
    /// <para><b>This is what made the word look soft once it could be scaled.</b> It shared the
    /// dial's build size, so at any scale above 1 a texture built for ~190px was being stretched to
    /// 300 or 500 by the GPU's sampler — magnifying a small bitmap rather than resampling the
    /// 1254px source it came from. Building at the drawn size means a bigger word genuinely
    /// resolves more of the original artwork instead of enlarging a reduction of it.</para>
    /// </summary>
    private int  _labelBuiltFor;
    private int  _labelWantSize;
    private long _labelWantSince;

    /// <summary>
    /// How long the wanted size must hold still before the word is rebuilt at it.
    ///
    /// <para>Dragging the scale slider changes the wanted size every frame, and a rebuild decodes a
    /// 1254px PNG and resamples it twice — sixty times a second while the mouse moves. Waiting for
    /// the number to settle costs a moment of the old texture being stretched mid-drag, which is
    /// exactly when nobody is judging sharpness.</para>
    /// </summary>
    private const long LabelRebuildDelayMs = 200;

    // ── the trail banners ────────────────────────────────────────────────────

    /// <summary>
    /// The two big one-shot announcements. 2:1 artwork, unlike everything else here, and white on
    /// transparent like everything else here — so the gold is ours to set and the vertical gradient
    /// is ours to paint.
    /// </summary>
    private const string TrailStartFile = "TrailStart!_digIcon.png";
    private const string TrailEndFile   = "TrailEnd!_digIcon.png";

    /// <summary>
    /// The artwork's shape. Position and size are live tuning values — see
    /// <see cref="DigTuning.BannerY"/> — but the aspect is a property of the PNG and is never a
    /// setting: stretching a banner is not something anyone wants a slider for.
    /// </summary>
    private const float BannerAspect = 1774f / 887f;

    /// <summary>
    /// More bands than the dial gets. The banner is several times taller on screen, and banding is a
    /// function of how many pixels each step has to cover — the same 24 steps that are invisible at
    /// 128px would be a visible ladder here.
    /// </summary>
    private const int BannerGradientBands = 40;

    private IDalamudTextureWrap? _bannerStart;
    private IDalamudTextureWrap? _bannerEnd;

    /// <summary>Width the banner pair was built for. 0 = nothing built yet.</summary>
    private int _bannerBuiltFor;

    private enum Banner { None, TrailStart, TrailEnd }

    private Banner _banner;
    private long   _bannerAt;
    private bool   _bannerArtWarned;

    /// <summary>
    /// Trail Start: slides in from the left as it fades up, holds, then fades out where it stands.
    /// Trail End: snaps into view in that same spot, holds, then leaves to the right as it fades.
    ///
    /// <para>The asymmetry is the point — arriving and departing should not look like the same
    /// event played twice. Start takes its time entering because nothing has happened yet; End is
    /// abrupt because something just did, and then it clears off rather than lingering over a
    /// finished trail.</para>
    /// </summary>
    private const float StartIn = 0.45f, StartHold = 1.7f, StartOut = 0.35f;
    private const float EndIn   = 0.18f, EndHold   = 1.3f, EndOut   = 0.55f;

    /// <summary>Travel distance for each slide, as a fraction of the viewport's width.</summary>
    private const float SlideInFrom = -0.60f;
    private const float SlideOutTo  =  0.60f;

    /// <summary>
    /// The trail's start and finish counts as last seen. <b>-1 means "not yet seen"</b>, which is
    /// what suppresses a banner for a trail that was already running when this overlay first drew —
    /// on a hot reload, for instance, where announcing a start that happened before the plugin
    /// existed would be a lie.
    ///
    /// <para>Read from the service rather than pushed by it, so every decision about what appears on
    /// screen stays on this side of the line. <see cref="DigTrailService"/> exposes plain counts;
    /// this decides a change in one is worth a banner.</para>
    /// </summary>
    private int _seenStarts   = -1;
    private int _seenFinishes = -1;

    // The word's lift and the headroom reserved for it inside the dial's window are GONE. Both were
    // workarounds for the word being drawn in a window sized to the dial, where any movement past a
    // few pixels was a CLIP rather than a move — raising the lift only removed more of the word.
    //
    // It has its own window now (DrawDialLabel), sized to wherever it has been put, so there is
    // nothing to reserve space against and nothing to keep the two numbers in step with. Position
    // and scale are live tuning values: DigTuning.LabelOffsetX / LabelOffsetY / LabelScale.

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

            var newCentre = BuildScaled(centre, targetPx, targetPx);
            var newOuter  = BuildScaled(outer,  targetPx, targetPx);

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

            Diag.Info($"[Dig] dial artwork resampled to {targetPx}px.");
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] dial artwork failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Decodes a PNG and reduces it to <paramref name="targetW"/> × <paramref name="targetH"/> by
    /// progressive halving plus one final resample. Returns null on any failure — the drawn dial is
    /// always a valid fallback.
    ///
    /// <para>Non-square because the two trail banners are 2:1 while everything else is square. The
    /// halving loop stops as soon as EITHER axis would fall below its target, so the short axis can
    /// never be reduced past what the final resample needs.</para>
    /// </summary>
    private IDalamudTextureWrap? BuildScaled(string path, int targetW, int targetH)
    {
        SKBitmap? working = null;

        try
        {
            working = SKBitmap.Decode(path);
            if (working == null) return null;

            // Halve while a halved copy would still be at least the target on BOTH axes. Each step
            // is an exact 2×2 average, which is the only way every source pixel gets a vote.
            while (working.Width / 2 >= targetW && working.Height / 2 >= targetH
                   && working.Width > 2 && working.Height > 2)
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
                new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                new SKSamplingOptions(SKCubicResampler.Mitchell));

            if (final == null) return null;

            try
            {
                return _texProvider.CreateFromRaw(
                    RawImageSpecification.Bgra32(targetW, targetH),
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

    /// <summary>
    /// Builds the CLUE! / DIG! word at the size it is actually drawn — see
    /// <see cref="_labelBuiltFor"/> for why that is not the dial's size.
    /// </summary>
    private void EnsureLabelArt(int targetPx)
    {
        if (targetPx <= 0 || targetPx == _labelBuiltFor) return;

        // Debounce: the wanted size has to hold still before a rebuild is worth doing.
        if (targetPx != _labelWantSize)
        {
            _labelWantSize  = targetPx;
            _labelWantSince = Environment.TickCount64;
            return;
        }

        if (Environment.TickCount64 - _labelWantSince < LabelRebuildDelayMs) return;

        _labelBuiltFor = targetPx;

        try
        {
            string? dir = Plugin.PluginInterface.AssemblyLocation.Directory?.FullName;
            if (string.IsNullOrEmpty(dir)) return;

            string folder = System.IO.Path.Combine(dir!, "digicons");

            // Built independently: a missing DIG! should not cost CLUE!. Each is simply not drawn
            // if it failed, which costs the word rather than the dial.
            var clue = BuildScaled(System.IO.Path.Combine(folder, ClueFile), targetPx, targetPx);
            var dig  = BuildScaled(System.IO.Path.Combine(folder, DigFile),  targetPx, targetPx);

            if (clue != null) { _labelClue?.Dispose(); _labelClue = clue; }
            if (dig  != null) { _labelDig?.Dispose();  _labelDig  = dig;  }

            Diag.Info($"[Dig] word artwork resampled to {targetPx}px.");
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] word artwork failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the two banners at the width they will be drawn, by the same route as the dial — see
    /// <see cref="EnsureDialArt"/> for why the GPU sampler is not trusted with the reduction.
    /// </summary>
    private void EnsureBannerArt(int targetW, int targetH)
    {
        if (targetW <= 0 || targetH <= 0 || targetW == _bannerBuiltFor) return;

        _bannerBuiltFor = targetW;

        try
        {
            string? dir = Plugin.PluginInterface.AssemblyLocation.Directory?.FullName;
            if (string.IsNullOrEmpty(dir)) return;

            string folder = System.IO.Path.Combine(dir!, "digicons");

            // Built independently: a missing End banner should not cost the Start one. Each is
            // simply not drawn if it failed.
            var start = BuildScaled(System.IO.Path.Combine(folder, TrailStartFile), targetW, targetH);
            var end   = BuildScaled(System.IO.Path.Combine(folder, TrailEndFile),   targetW, targetH);

            if (start != null) { _bannerStart?.Dispose(); _bannerStart = start; }
            if (end   != null) { _bannerEnd?.Dispose();   _bannerEnd   = end;   }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] banner artwork failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Where a banner is in its life: its opacity, and how far it is displaced sideways as a
    /// fraction of the viewport's width. False once it is over.
    /// </summary>
    private static bool BannerFrame(Banner kind, float t, out float alpha, out float slide)
    {
        alpha = 0f;
        slide = 0f;

        if (t < 0f) return false;

        bool start = kind == Banner.TrailStart;

        float fadeIn  = start ? StartIn   : EndIn;
        float hold    = start ? StartHold : EndHold;
        float fadeOut = start ? StartOut  : EndOut;

        if (t < fadeIn)
        {
            float u = t / fadeIn;
            alpha = u;

            // Eased OUT, so it decelerates into place instead of arriving at full speed and
            // stopping dead. Only Start travels on the way in.
            slide = start ? SlideInFrom * (1f - EaseOut(u)) : 0f;
            return true;
        }

        t -= fadeIn;
        if (t < hold) { alpha = 1f; return true; }

        t -= hold;
        if (t < fadeOut)
        {
            float u = t / fadeOut;
            alpha = 1f - u;

            // Eased IN: it leaves reluctantly and then accelerates away. Only End travels out.
            slide = start ? 0f : SlideOutTo * EaseIn(u);
            return true;
        }

        return false;
    }

    private static float EaseOut(float u) { float v = 1f - u; return 1f - v * v * v; }
    private static float EaseIn(float u) => u * u * u;

    // ── the light burst ──────────────────────────────────────────────────────

    /// <summary>
    /// How many slices each beam is cut into along its length.
    ///
    /// <para>32, not 9. Nine left visible steps in what is supposed to be a smooth fade; the cost of
    /// a slice is one quad, so there is no reason to be frugal here.</para>
    /// </summary>
    private const int RaySegments = 32;

    /// <summary>
    /// Slices ACROSS a beam, for the left/right edge fade. Capped against the beam count so the
    /// burst cannot blow the draw list's index budget — see the note where it is computed.
    /// </summary>
    private const int RayAngularSlices = 7;

    /// <summary>
    /// How much wider than its own slot a beam gets at full edge fade. Just under 2, so a beam's
    /// faded edge reaches the CENTRE of each neighbour and the join is covered from both sides.
    /// </summary>
    private const float RayOverlapAtFullFade = 1.9f;

    /// <summary>
    /// Stable pseudo-random in [0,1) from a beam index and a salt, so each beam gets its own width,
    /// speed, reach and starting phase.
    ///
    /// <para><b>Hashed rather than drawn from a random source, deliberately.</b> These values are
    /// read every frame and have to be identical every time or the burst would reshuffle itself
    /// sixty times a second. A hash gives per-beam variety that is nonetheless completely stable,
    /// with no table to allocate, keep, or invalidate when the beam count changes.</para>
    /// </summary>
    private static float Hash01(int index, int salt)
    {
        unchecked
        {
            uint h = (uint)(index * 374761393 + salt * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0x1000000;
        }
    }

    /// <summary>
    /// A burst of light behind the dial: wedges radiating from under the backing disc, each
    /// breathing in and out on its own clock.
    ///
    /// <para><b>Sliced along its length, for the third time in this file and for the same reason.</b>
    /// A ray has to fade to nothing at its tip or it reads as a painted triangle, and
    /// <c>AddQuadFilled</c> takes one colour per quad — so the falloff is built from several quads
    /// at descending alpha rather than from a gradient. The slices share exact edges, so the ray is
    /// continuous; only its alpha steps.</para>
    ///
    /// <para><b>Drawn into the BACKGROUND draw list, not the dial's window.</b> The rays reach far
    /// past the dial and a window sized to the artwork would simply cut them off — the same trap the
    /// CLUE! lettering hit. Enlarging the dial's window is not the answer either: that window takes
    /// input because it is a button, so a bigger one would swallow clicks aimed at the game across a
    /// much larger patch of screen. The background list is unclipped, takes no input, and paints
    /// under every ImGui window, which is exactly the layer this belongs on.</para>
    ///
    /// <para><b>It will look weak on snow, and that is a limit rather than a bug.</b> A light effect
    /// wants additive blending; a draw list gives normal alpha blending, so white over white is
    /// white. That is why the colour is a knob — on pale ground the burst needs a colour with
    /// somewhere to go.</para>
    /// </summary>
    private void DrawRays(ImDrawListPtr drawList, Vector2 centre, float innerR,
                          float fade, float uiScale, float dt)
    {
        int count = Math.Clamp(DigTuning.RayCount, 3, MaxRays);

        float reach   = MathF.Max(4f, DigTuning.RayReach) * uiScale;
        float opacity = Math.Clamp(DigTuning.RayOpacity, 0f, 1f) * fade;
        if (opacity <= 0.002f) return;

        var  rgb     = DigTuning.RayColor;
        float falloff = MathF.Max(0.2f, DigTuning.RayFalloff);
        float minLen  = Math.Clamp(DigTuning.RayMinLength, 0f, 1f);

        float widthVar = Math.Clamp(DigTuning.RayWidthVariance, 0f, 3f);
        float speedVar = Math.Clamp(DigTuning.RaySpeedVariance, 0f, 1f);
        float reachVar = Math.Clamp(DigTuning.RayReachVariance, 0f, 1f);

        // Widths are weights normalised to a full turn, so the beams TILE — no gaps. Gaps were what
        // made the last version read as a cog rather than as light: a burst is a disc of light with
        // an uneven outer edge, not a ring of separate spikes with dark between them.
        Span<float> width = stackalloc float[MaxRays];
        float total = 0f;

        for (int i = 0; i < count; i++)
        {
            width[i] = 0.35f + Hash01(i, 1) * widthVar;
            total   += width[i];
        }

        if (total <= 0f) return;

        // ANTIALIASED FILL OFF for the whole burst, and this is the fix for the white lines.
        //
        // A beam's fade is built from slices at descending alpha, and ImGui feathers the edge of
        // every filled poly by about a pixel. Two abutting slices therefore each lay down a
        // half-covered edge over the same pixels, and the two blends ADD — producing a bright seam
        // exactly where the two were supposed to join invisibly. It reads as a hard line ruled
        // across the beam, which is precisely what it is. With feathering off the slices meet on
        // exact pixel boundaries and the ramp is continuous.
        var savedFlags = drawList.Flags;
        drawList.Flags &= ~ImDrawListFlags.AntiAliasedFill;

        float edgeFade = Math.Clamp(DigTuning.RayEdgeFade, 0f, 1f);

        // Widen every beam by the same measure the edges are softened by. Beams tile the turn
        // EXACTLY, so a beam that faded to nothing within its own slot would meet its neighbour
        // where both are zero — a dark seam at every join, which is the gap problem again wearing a
        // different hat. Spreading each beam over its neighbours puts its faded edge ON TOP of
        // theirs instead of beside it, so the boundary stays lit. At zero fade there is no spread,
        // because a hard edge has nothing to cover.
        float overlap = 1f + edgeFade * (RayOverlapAtFullFade - 1f);

        // Slices across a beam, bounded by how many beams there are. ImDrawIdx is 16 bits, so a
        // draw list past about 65k indices is relying on the backend's vertex-offset support to
        // keep rendering correctly; at 48 beams an unbounded 7 slices would be 10,752 quads and
        // 64k indices from this effect alone. Staying well under is cheaper than finding out.
        int slices = Math.Clamp(126 / count, 3, RayAngularSlices);

        float cursor = 0f;

        for (int i = 0; i < count; i++)
        {
            float span = MathF.Tau * width[i] / total;

            float mid  = cursor + span * 0.5f;
            float half = span * 0.5f * overlap;
            cursor    += span;

            // Own speed, own reach, own starting point in the cycle. All hashed off the index so
            // they are stable frame to frame — see Hash01.
            float rate = 1f + (Hash01(i, 2) * 2f - 1f) * speedVar;

            _rayPhase[i] += dt * MathF.Tau * MathF.Max(0.01f, DigTuning.RaySpeed) * MathF.Max(0.05f, rate);
            if (_rayPhase[i] > MathF.Tau) _rayPhase[i] %= MathF.Tau;

            float wave = 0.5f + 0.5f * MathF.Sin(_rayPhase[i] + Hash01(i, 4) * MathF.Tau);

            // Not every beam reaches as far, even at full stretch.
            float span01 = 1f - Hash01(i, 3) * reachVar;
            float len    = reach * span01 * (minLen + (1f - minLen) * wave);
            if (len <= 1f) continue;

            for (int s = 0; s < RaySegments; s++)
            {
                float t0 = s       / (float)RaySegments;
                float t1 = (s + 1) / (float)RaySegments;

                // Alpha along the beam, from the slice's midpoint. Reaches exactly zero at the tip,
                // so a beam ends by running out rather than by stopping.
                float tm     = (t0 + t1) * 0.5f;
                float radial = opacity * MathF.Pow(1f - tm, falloff);
                if (radial <= 0.0015f) continue;

                float r0 = innerR + len * t0;
                float r1 = innerR + len * t1;

                for (int k = 0; k < slices; k++)
                {
                    float u0 = -1f + 2f * k       / slices;
                    float u1 = -1f + 2f * (k + 1) / slices;

                    // Alpha ACROSS the beam. A cosine rather than a straight taper, because a
                    // linear fade leaves a visible crease down the middle where the two sides meet
                    // at a corner — cosine is flat at the centre and flat at the edges, so neither
                    // the middle nor the join announces itself.
                    float um    = (u0 + u1) * 0.5f;
                    float shape = MathF.Cos(um * MathF.PI * 0.5f);
                    float a     = radial * (1f - edgeFade * (1f - shape));
                    if (a <= 0.0015f) continue;

                    // Wound consistently around the ring — AddQuadFilled goes through
                    // AddConvexPolyFilled, which renders a figure-of-eight as two slivers rather
                    // than erroring.
                    drawList.AddQuadFilled(
                        centre + Polar(mid + u0 * half, r0),
                        centre + Polar(mid + u1 * half, r0),
                        centre + Polar(mid + u1 * half, r1),
                        centre + Polar(mid + u0 * half, r1),
                        Tint(rgb, a));
                }
            }
        }

        drawList.Flags = savedFlags;
    }

    private static Vector2 Polar(float angle, float radius) =>
        new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);

    /// <summary>
    /// Draws whichever banner is running. Its own full-width strip of a window, so a banner sliding
    /// off the side is not clipped by a window sized to the artwork.
    /// </summary>
    private void DrawBanner()
    {
        if (_banner == Banner.None) return;

        float t = (Environment.TickCount64 - _bannerAt) / 1000f;

        if (!BannerFrame(_banner, t, out float alpha, out float slide))
        {
            _banner = Banner.None;
            return;
        }

        var viewport = ImGui.GetMainViewport();

        int w = (int)MathF.Round(viewport.Size.X * Math.Clamp(DigTuning.BannerWidth, 0.10f, 1f));
        int h = (int)MathF.Round(w / BannerAspect);
        if (w <= 0 || h <= 0) return;

        float uiScale = UiScale.Factor;

        // Room for the outline and shadow to reach past the artwork without being cut off.
        int pad = (int)MathF.Ceiling((OutlinePx + ShadowPx) * uiScale) + 2;

        EnsureBannerArt(w, h);

        var art = _banner == Banner.TrailStart ? _bannerStart : _bannerEnd;

        if (art == null)
        {
            // Said once per banner rather than swallowed. A banner that never draws because its PNG
            // is missing looks exactly like a banner that was never triggered, and that ambiguity is
            // what makes "it usually doesn't appear" hard to chase.
            if (!_bannerArtWarned)
            {
                _bannerArtWarned = true;
                Diag.Error($"[Dig] {_banner} banner has no artwork — check digicons\\ beside the DLL.");
            }

            _banner = Banner.None;
            return;
        }

        float top = viewport.Pos.Y + viewport.Size.Y * Math.Clamp(DigTuning.BannerY, 0f, 0.90f);

        if (!BeginHud("##tc_dig_banner", new Vector2(viewport.Pos.X, top - pad),
                      (int)viewport.Size.X, h + pad * 2)) return;

        var drawList = ImGui.GetWindowDrawList();

        // The slide and the placement offset are both fractions of the viewport's width, so they
        // add in the same units and the banner still travels the same PROPORTION of the screen
        // wherever it has been placed.
        float x = viewport.Pos.X + (viewport.Size.X - w) * 0.5f
                + (slide + Math.Clamp(DigTuning.BannerX, -0.5f, 0.5f)) * viewport.Size.X;

        var min  = new Vector2(MathF.Round(x), MathF.Round(top));
        var size = new Vector2(w, h);

        float shadow  = ShadowPx  * uiScale;
        float outline = OutlinePx * uiScale;

        // Motion echoes, BEHIND everything and only while the banner is actually travelling.
        //
        // Each ghost is the banner drawn where it genuinely WAS a few frames ago — the slide is
        // re-evaluated at an earlier time rather than offset by a fixed number of pixels. That is
        // what makes the trail follow the easing: the ghosts bunch together as the banner
        // decelerates into place and stretch apart as it accelerates away, which is what motion
        // blur does. Evenly spaced pixel offsets would look identical whether it was flying or
        // nearly stopped.
        //
        // No outline and no shadow on a ghost. They are a smear of light, and stamping each one
        // would cost eight draws apiece to make the trail look like a stack of solid copies.
        if (DigTuning.BannerEcho)
        {
            int   echoes  = Math.Clamp(DigTuning.BannerEchoCount, 1, 16);
            float step    = Math.Clamp(DigTuning.BannerEchoStep, 0.005f, 0.30f);
            float falloff = Math.Clamp(DigTuning.BannerEchoFalloff, 0.05f, 0.95f);

            for (int k = echoes; k >= 1; k--)
            {
                if (!BannerFrame(_banner, t - k * step, out _, out float wasSlide)) continue;

                float dx = (wasSlide - slide) * viewport.Size.X;

                // Under a pixel of travel is not movement; during the hold every ghost would land
                // on the banner itself and merely darken it.
                if (MathF.Abs(dx) < 1f) continue;

                GradientImage(drawList, art.Handle,
                              min + new Vector2(dx, 0f), min + size + new Vector2(dx, 0f),
                              DigTuning.BannerTopColor, DigTuning.BannerBottomColor,
                              alpha * MathF.Pow(falloff, k), BannerGradientBands);
            }
        }

        // Same three layers as the dial, and in the same order: depth, rim, face. The banner is
        // large text over open world, which is exactly the case a bare tint cannot survive.
        drawList.AddImage(art.Handle, min + new Vector2(shadow, shadow),
                          min + size + new Vector2(shadow, shadow),
                          Vector2.Zero, Vector2.One, Tint(Ink, 0.45f * alpha));

        DrawEmblem(drawList, art, min, size,
                   DigTuning.BannerTopColor, DigTuning.BannerBottomColor,
                   alpha, outline, BannerGradientBands);

        EndHud();
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
    /// One accumulated phase per light ray, so each breathes on its own schedule.
    ///
    /// <para><b>An array rather than one shared phase with per-ray offsets, and that is a precision
    /// decision, not a stylistic one.</b> Rays run at DIFFERENT rates — that is what stops the burst
    /// pumping in unison — so a single accumulator would have to be multiplied by each ray's rate,
    /// and it could then only be wrapped at a value that is a whole number of cycles for every rate
    /// at once. There is no such value. Left unwrapped it grows until <c>sin</c> loses precision and
    /// the burst starts juddering after a long session. Wrapped per ray, each stays in [0, τ)
    /// forever and none of that arises.</para>
    /// </summary>
    private const int MaxRays = 48;
    private readonly float[] _rayPhase = new float[MaxRays];

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

    /// <summary>
    /// Whether the active test is reporting a distance at all this frame. Separate from
    /// <see cref="_radarCloseness"/>, which merely holds its last value when the signal stops.
    /// </summary>
    private bool _inRange;

    /// <param name="onDig">
    /// Runs when the dial is clicked. Supplied by the plugin rather than reached for here, so this
    /// class stays presentation — it draws what a test reports and forwards a click, and does not
    /// itself decide that a click means "dig".
    /// </param>
    /// <param name="onRecall">
    /// Runs when the dial is clicked from OUT of range, where a dig would find nothing. Separate
    /// from <paramref name="onDig"/> for the same reason that one is supplied rather than reached
    /// for: this class draws what a test reports and forwards a click, and does not itself decide
    /// what a click in a given state means.
    /// </param>
    public DigHuntOverlay(ITextureProvider texProvider, Action? onDig = null, Action? onRecall = null)
    {
        _texProvider = texProvider;
        _onDig       = onDig;
        _onRecall    = onRecall;
    }

    public void Dispose()
    {
        _reminder?.Dispose(); _reminder = null;
        _radar?.Dispose();    _radar    = null;

        _labelClue?.Dispose(); _labelClue = null;
        _labelDig?.Dispose();  _labelDig  = null;

        _bannerStart?.Dispose(); _bannerStart = null;
        _bannerEnd?.Dispose();   _bannerEnd   = null;

        // These ARE ours — built by CreateFromRaw rather than borrowed from the shared cache.
        _dialOuter?.Dispose();  _dialOuter  = null;
        _dialCentre?.Dispose(); _dialCentre = null;
    }

    public void Draw(DigTests tests)
    {
        // Before the early-out: a banner outlives the thing that triggered it. The End banner in
        // particular is thrown by a trail that is on its way to stopping, so gating this on there
        // being an active test would cut it off partway through.
        WatchTrail(tests.Trail);
        DrawBanner();

        var active = tests.Active;

        if (active == null)
        {
            // Nothing running: the next test starts from silence rather than inheriting a
            // half-faded ring from the last one.
            _radarFade     = 0f;
            _radarPhase    = 0f;
            _shinePhase    = 0f;
            _reminderAlpha = 0f;
            _reminderUntil = 0;
            _reminderText  = string.Empty;
            _digWordAlpha  = 0f;
            _clueWordAlpha = 0f;
            _hiddenForDig  = false;
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

        // Held for the draw, which needs to tell "far away" from "no signal" — two states the
        // closeness value alone cannot distinguish, since it simply stops existing out of range.
        _inRange = inRange;

        // Linear ramp towards the target, so a full fade takes exactly the configured time and a
        // partial one takes proportionally less — which is what makes crossing the boundary twice
        // in quick succession look like one continuous movement rather than two events.
        // THE DIAL IS UP FOR THE WHOLE RUN, not only within radar range.
        //
        // It used to appear and disappear with the signal, which made it a proximity readout and
        // nothing else. Keeping it on screen the whole time gives it a second job that it could not
        // have while it kept vanishing: out of range it is a grey button that says the clue again.
        // The pulse and the colour still carry the proximity, so nothing is lost by it being there.
        float seconds = MathF.Max(0.05f, DigTuning.RadarFadeSeconds);
        float step    = ImGui.GetIO().DeltaTime / seconds;
        float target  = 1f;

        if      (_radarFade < target) _radarFade = MathF.Min(target, _radarFade + step);
        else if (_radarFade > target) _radarFade = MathF.Max(target, _radarFade - step);

        // The DIG! word's own envelope, integrated HERE rather than in the draw so it keeps moving
        // on frames the dial is not drawn — an envelope that only advances while its subject is on
        // screen can never finish fading out.
        // Gated on the dig too, not only on range. Digging the right spot takes the button away and
        // the word has to leave with it — but by fading, which means the target has to fall here
        // rather than the word simply stopping being drawn.
        bool  onSpot     = inRange && closeness!.Value >= 1f && !_hiddenForDig;
        float wordTarget = onSpot ? 1f : 0f;
        float wordStep   = ImGui.GetIO().DeltaTime / (onSpot ? WordFadeIn : WordFadeOut);

        if      (_digWordAlpha < wordTarget) _digWordAlpha = MathF.Min(wordTarget, _digWordAlpha + wordStep);
        else if (_digWordAlpha > wordTarget) _digWordAlpha = MathF.Max(wordTarget, _digWordAlpha - wordStep);

        // CLUE! yields the slot to DIG! the frame it is wanted — instantly, because the two words
        // sit in the same place and anything gradual prints one through the other. It returns on a
        // ramp, which is safe: by then DIG! is fully gone and there is nothing to collide with.
        //
        // The second condition is what stops it flashing back after a dig. Digging retires the clue
        // and fades DIG! out, but those two run at DIFFERENT rates — DIG! is gone in 0.45s while the
        // clue takes a full second to leave. Gating only on DIG! therefore let CLUE! pop back over
        // a clue that was already halfway out of the door, purely because the faster envelope
        // finished first. Keying on whether the clue is still WANTED rather than on whether it
        // still has opacity means a clue on its way out never brings its heading back with it.
        bool clueWanted = _digWordAlpha <= 0.002f
                          && Environment.TickCount64 < _reminderUntil;

        if (!clueWanted)
            _clueWordAlpha = 0f;
        else
            _clueWordAlpha = MathF.Min(1f, _clueWordAlpha + ImGui.GetIO().DeltaTime / WordFadeIn);

        if (_radarFade <= 0.002f)
        {
            _radarFade  = 0f;
            _radarPhase = 0f;      // fully gone — next appearance starts from a known point
            return;
        }

        // Hidden for the dig, but only once the press has finished playing: the click has to be
        // seen to land before the button is allowed to disappear, or it reads as the dial
        // vanishing on contact rather than as a button being pressed.
        bool showDial = !(_hiddenForDig && Environment.TickCount64 - _pressedAtMs >= PressMs);

        if (showDial) DrawRadar(_radarCloseness, _radarFade);

        // The word is drawn WHETHER OR NOT THE DIAL IS, and after it.
        //
        // After, because its window has to be begun later than the dial's to sit on top of it — the
        // lettering overlaps the rim by design. Whether or not, because a dig takes the button away
        // instantly and the word is supposed to fade: while it was drawn inside the dial's early
        // returns it could only ever vanish along with it.
        DrawDialLabel(UiScale.Factor, _radarCloseness, _radarFade);
    }

    /// <summary>True while the dial is withheld for a dig started from the right spot.</summary>
    private bool _hiddenForDig;

    /// <summary>
    /// Turns the trail's two state transitions into the two banners.
    ///
    /// <para><b>Edges, not levels.</b> The trail reports "running" and "finished" for as long as
    /// each lasts, so reacting to the state itself would restart the banner every frame. Only the
    /// moment it changes counts.</para>
    ///
    /// <para>Start is deliberately gated on <c>!IsFinished</c> as well. A trail is briefly both
    /// active and finished while its result is on screen, and without that the End banner's own
    /// stop — which flips active back to false and then true on the next run — could be read as a
    /// fresh start.</para>
    /// </summary>
    private void WatchTrail(DigTrailService trail)
    {
        // Compare COUNTS, never levels. The previous version watched IsActive/IsFinished for a
        // change of level and dropped the start banner whenever a trail was restarted during the
        // fifteen seconds the last one's result was still up — active-and-done to active-and-running
        // is not an edge, so nothing fired. Restarting quickly is how a trail gets tested, so that
        // was most starts. See DigTrailService.StartCount.
        int starts    = trail.StartCount;
        int finishes  = trail.FinishCount;

        if (starts != _seenStarts)
        {
            bool first = _seenStarts < 0;
            _seenStarts = starts;
            if (!first) Show(Banner.TrailStart);
        }

        if (finishes != _seenFinishes)
        {
            bool first = _seenFinishes < 0;
            _seenFinishes = finishes;
            if (!first) Show(Banner.TrailEnd);
        }
    }

    private void Show(Banner kind)
    {
        _banner   = kind;
        _bannerAt = Environment.TickCount64;
    }

    /// <summary>
    /// Plays a banner on demand, with no trail involved. The lab's preview buttons.
    ///
    /// <para>It exists because the real trigger is unusable for judging the animation: starting a
    /// trail means scrolling to the bottom of the lab, clicking Start, and then getting back up to
    /// the banner settings before a two-second animation has finished. Every tweak was a race
    /// against the thing being tweaked.</para>
    ///
    /// <para>Safe with nothing running — the banner is drawn ahead of the no-active-test early-out,
    /// because an End banner already has to outlive the trail that threw it.</para>
    /// </summary>
    public void PreviewBanner(bool end) => Show(end ? Banner.TrailEnd : Banner.TrailStart);

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
    /// <para>Light yellow, ringed in white, over a dark drop shadow — see <see cref="ClueFace"/>.
    /// This is the same contrast problem the dial's backing disc solves, solved the way type solves
    /// it: plain white was unreadable on snow, and no choice of face colour fixes that on its own
    /// because some ground in Eorzea is always going to be that colour.</para>
    /// </summary>
    private void DrawReminder(IDigTest test)
    {
        string text = test.Subtitle ?? string.Empty;

        // A line that has CHANGED is new information, and gets its own full showing — see
        // _reminderText. Checked before the visibility test, so a clue arriving while nothing is on
        // screen brings the reminder back rather than being missed.
        if (!string.Equals(text, _reminderText, StringComparison.Ordinal))
        {
            _reminderText = text;
            if (!string.IsNullOrWhiteSpace(text)) ShowReminder();
        }

        bool want = !string.IsNullOrWhiteSpace(text)
                    && Environment.TickCount64 < _reminderUntil;

        // Ramp toward the target rather than replaying a curve from a start time. Extending the
        // hold while it is fading out simply reverses the ramp from wherever it had got to.
        float dt     = ImGui.GetIO().DeltaTime;
        float target = want ? 1f : 0f;
        float step   = dt / MathF.Max(0.05f, want ? ReminderIn : ReminderOut);

        if      (_reminderAlpha < target) _reminderAlpha = MathF.Min(target, _reminderAlpha + step);
        else if (_reminderAlpha > target) _reminderAlpha = MathF.Max(target, _reminderAlpha - step);

        if (_reminderAlpha <= 0.002f) { _reminderAlpha = 0f; return; }

        float alpha   = _reminderAlpha;
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

        if (tex.HasValue)
        {
            // Drawn through the gradient rather than ImGui.Image, so the clue falls off downward
            // like everything else on this HUD. Tinted at WHITE, so the gradient is the only thing
            // it changes — the face and rim colours are decided inside the surface, where they can
            // differ from each other.
            //
            // The shadow layer rides along untouched: a tint MULTIPLIES, and black multiplied by
            // anything is still black. That is the whole reason one surface can carry all three
            // layers — no second render pass, and they cannot drift out of register with each
            // other because they are the same texture.
            var min = ImGui.GetCursorScreenPos();

            GradientImage(ImGui.GetWindowDrawList(), tex.Value,
                          min, min + new Vector2(physW, physH), Vector3.One, 1f,
                          GradientBands, DigTuning.ClueGradient ? GradientDrop : 1f);

            ImGui.Dummy(new Vector2(physW, physH));
        }

        EndHud();
    }

    /// <summary>
    /// Asks the reminder to be visible for a full hold from now.
    ///
    /// <para>Sets an absolute deadline rather than adding to one, so hammering the dig button keeps
    /// the clue up for one full duration instead of stacking a queue of them.</para>
    /// </summary>
    private void ShowReminder() =>
        _reminderUntil = Environment.TickCount64 + (long)(ReminderHold * 1000f);

    /// <summary>
    /// Ends the current showing early. The opacity still ramps down over the normal fade-out — this
    /// only withdraws the request to be visible, it does not blank anything.
    /// </summary>
    private void HideReminder() => _reminderUntil = Environment.TickCount64;

    private static Node BuildReminder(string text, float alpha)
    {
        var root = new Node().WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fixed; s.Width  = ReminderW;
            s.HeightMode = SizeMode.Fixed; s.Height = ReminderH;
        });

        // Back to front: shadow, rim, face. Every layer is the same string in the same fixed-width
        // box, so they cannot wrap differently and split apart. Each of the two lower layers is
        // skippable from the lab — with both off this is plain coloured type, which is a legitimate
        // thing to want on ground that already contrasts.
        float shadowPx = ClueShadowPx;
        if (shadowPx > 0f)
            root.AppendChild(TextLayer(text, new Vector2(shadowPx, shadowPx),
                                       PColor.Black.WithOpacity(0.80f * alpha)));

        // The rim, stamped around the face the same way the dial's artwork outline is — eight
        // directions, because at four the diagonals of a letter get an edge on their flats and
        // none on their corners.
        float rimPx = ClueRimPx;
        if (rimPx > 0f)
        {
            var rim = ClueRim;

            for (int i = 0; i < ClueRimSteps; i++)
            {
                float a = MathF.Tau * i / ClueRimSteps;

                root.AppendChild(TextLayer(text,
                    new Vector2(MathF.Cos(a) * rimPx, MathF.Sin(a) * rimPx),
                    rim.WithOpacity(alpha)));
            }
        }

        root.AppendChild(TextLayer(text, Vector2.Zero, ClueFace.WithOpacity(alpha)));

        return root;
    }

    private static Node TextLayer(string text, Vector2 offset, PColor colour)
        => new Node().WithText(text).WithStyle(s =>
        {
            s.Position     = PositionMode.Absolute;
            s.Left         = offset.X;
            s.Top          = offset.Y + ClueTopInset;
            s.WidthMode    = SizeMode.Fixed; s.Width  = ReminderW;

            // Fit, NOT the surface height. A fixed-height text node lets the framework centre the
            // line vertically inside it, which put the text far lower than ReminderGapPx claimed
            // and made that constant nearly inert. Fit puts the first line at the top, so the gap
            // above is the only thing deciding where the text sits.
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = DigTuning.ClueFontSize;
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
        var (pos, phys) = DialRect(uiScale);

        // The one HUD window that takes input — it is a button. Sized to the dial and nothing more,
        // so the amount of screen that can swallow a click is one small circle for the few seconds
        // it is up. The word used to be drawn in here too and needed headroom reserved above; it has
        // its own window now, so this is back to exactly the artwork's size.
        if (!BeginHud("##tc_dig_radar", pos, phys, phys, acceptInput: true)) return;

        EnsureDialArt(phys);

        bool solid = _inRange && closeness >= 1f;

        // Grey with no signal; deep blue at the edge of radar range, green as it closes, yellow in
        // the last stretch. The pulse says "something is here"; the colour says how near, so the
        // two carry different information instead of both restating proximity.
        Vector3 baseRgb = !_inRange ? Rgb(IdleGrey)
                        : solid     ? Rgb(DigCol)
                                    : Ramp(closeness);

        float press     = PressAmount();
        var   accentRgb = PressedAccent(baseRgb, press);

        float wave = 0.5f + 0.5f * MathF.Sin(_radarPhase);

        // The trough is 0.55, not 0.18. A pulse that fades almost to nothing spends half its cycle
        // illegible, which reads as a flicker rather than a heartbeat — and legibility was the
        // complaint. The swing is still obvious against a dark backing; it just never disappears.
        // Steady out of range. The pulse means "something is being detected", so pulsing at nothing
        // would be the dial claiming a signal it does not have — and an idle button that breathes
        // is asking for attention it has not earned.
        float fill = !_inRange ? 0.80f
                   : solid     ? 1f
                               : 0.55f + 0.45f * wave;

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

            // 0. The light behind, when a dig would land. Painted into the BACKGROUND draw list
            //    rather than this window: it reaches far past the dial, and this window is sized to
            //    the artwork AND takes input because it is a button — so growing it to fit would
            //    swallow game clicks across a much larger patch of screen. See DrawRays.
            if (solid && DigTuning.RaysEnabled)
            {
                DrawRays(ImGui.GetBackgroundDrawList(),
                         anchor + size * 0.5f,
                         phys * 0.5f * BackingFraction,
                         fade, uiScale, ImGui.GetIO().DeltaTime);
            }

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

            // 3. Rim: outline, inner white-to-accent edge, then the fill on the steep ramp.
            DrawEmblem(drawList, dialOuter, origin, size, accentRgb, ringAlpha, outline, GradientBands);

            // 4. Figure. Its alpha carries the pulse, so the outline pulses with it — an outline
            //    holding steady while its fill breathes reads as two objects.
            float centreAlpha = fill * fade;
            DrawEmblem(drawList, dialCentre, origin, size, accentRgb, centreAlpha, outline, GradientBands);

            // The word is NOT drawn here. It moves and scales freely now, so it lives in its own
            // window drawn after this one — see DrawDialLabel.

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

            // A dig on the RIGHT spot retires the clue; a miss keeps it up, because it is still the
            // answer to a question the player has not solved.
            //
            // Retired by an EARLY EXIT — the deadline is pulled back to now so the envelope ramps
            // down normally — rather than by clearing it. A clue that vanishes on the frame of the
            // click reads as a glitch; one that fades reads as being finished with.
            //
            // This costs nothing on the way to the next clue, which is the trap an earlier version
            // fell into by clearing outright and killing the arriving clue with it. The next clue
            // does not come from here at all: it turns up when the dig completes, and the CHANGE of
            // line triggers its own fresh showing. The two are independent, so retiring the old one
            // cannot suppress the new one.
            if (onSpot) HideReminder();
            else        ShowReminder();

            // Out of range the dial is not a dig button, it is a "say that again" button. Digging
            // where nothing is detected only costs the player the animation, and the grey makes a
            // promise that pressing it does something useful.
            if (_inRange) _onDig?.Invoke();
            else          _onRecall?.Invoke();
        }

        EndHud();
    }

    /// <summary>
    /// Where the dial sits and how big it is. Shared, because the word is positioned relative to it
    /// from a completely different call path now — two copies of this arithmetic would drift the
    /// first time the drop or the size changed.
    /// </summary>
    private static (Vector2 pos, int phys) DialRect(float uiScale)
    {
        int phys = (int)(RadarSize * uiScale);

        var viewport = ImGui.GetMainViewport();

        // The reminder below adds the SAME drop, so dial, word and clue all move as one and the gap
        // between dial and clue is decided only by ReminderGapPx.
        return (new Vector2(
                    viewport.Pos.X + (viewport.Size.X - phys) * 0.5f,
                    viewport.Pos.Y + viewport.Size.Y * TopFraction + DropPx * uiScale),
                phys);
    }

    /// <summary>
    /// Click feedback, 1 at the instant of the press decaying to 0 over <see cref="PressMs"/>.
    /// A linear decay reads as a press-and-release; a symmetric in-out reads as a pulse and gets
    /// confused with the one the dial is already running.
    /// </summary>
    private float PressAmount()
    {
        if (_pressedAtMs <= 0) return 0f;

        long since = Environment.TickCount64 - _pressedAtMs;
        return since >= PressMs ? 0f : 1f - since / (float)PressMs;
    }

    /// <summary>
    /// Darker and less saturated while held, so a pressed control reads as taking the click rather
    /// than merely moving.
    /// </summary>
    private static Vector3 PressedAccent(Vector3 rgb, float press) =>
        Vector3.Lerp(rgb, Desaturate(rgb) * 0.62f, press);

    /// <summary>
    /// The CLUE! / DIG! word, in a window of its own.
    ///
    /// <para><b>It is separate because it is now freely placed and scaled.</b> It used to be drawn
    /// inside the dial's window at the dial's own rect, which meant any lift past a few pixels was
    /// CLIPPED rather than moved — the window was exactly the artwork's size. That was patched once
    /// by reserving headroom above the dial, which works only while the offset is a small upward
    /// nudge and falls apart the moment the word can also move sideways or grow.</para>
    ///
    /// <para>Growing the dial's own window instead would have been worse: that window accepts input
    /// because it is the dig button, so a bigger one swallows game clicks over a bigger patch of
    /// screen. This one takes no input at all.</para>
    /// </summary>
    private void DrawDialLabel(float uiScale, float closeness, float fade)
    {
        // TWO LAYERS, never both visible at once.
        //
        // Picking `solid ? DIG! : CLUE!` was the original bug: `solid` flips the instant the player
        // crosses the dig radius, so walking out SWAPPED the artwork rather than fading it, and
        // DIG!'s envelope — ramping down correctly the whole time — was never the one being drawn.
        //
        // Drawing both and crossfading fixed the fade and introduced a worse problem: the two words
        // sit in the SAME place, so any frame where both are partly opaque prints one through the
        // other. _clueWordAlpha is what settles it — DIG! takes the slot the instant it is wanted
        // and CLUE! is gone that frame, with no overlap at any point.
        //
        // CLUE! is still multiplied by the clue line's own opacity, so it can only ever be up while
        // there is a clue beneath it to label. DIG! has its own envelope, because it marks a state
        // the player is standing in rather than a message that was shown. Both are bounded by the
        // dial's fade, so neither outlives what it sits on.
        float clueAlpha = _reminderAlpha * _clueWordAlpha * fade;
        float digAlpha  = _digWordAlpha  * fade;

        if (clueAlpha <= 0.002f && digAlpha <= 0.002f) return;

        var (pos, phys) = DialRect(uiScale);

        float scale = Math.Clamp(DigTuning.LabelScale, 0.2f, 3f);

        // A WHOLE number of pixels, and built at that number. A fractional destination would have
        // the GPU resample a 1:1 blit across a half-pixel offset and hand back the sharpness the
        // resampling just bought — the same rule the dial itself follows.
        int side = Math.Max(1, (int)MathF.Round(phys * scale));

        EnsureLabelArt(side);

        if (_labelClue == null && _labelDig == null) return;

        float press = PressAmount();
        float sink  = press * PressOffsetPx * uiScale;

        // Scaled about the dial's CENTRE, then offset. Centring first is what keeps the offsets
        // meaning the same thing at every scale — grow it about the top-left instead and every
        // size change would also walk the word sideways.
        var min = new Vector2(
            MathF.Round(pos.X + (phys - side) * 0.5f + DigTuning.LabelOffsetX * uiScale + sink),
            MathF.Round(pos.Y + (phys - side) * 0.5f + DigTuning.LabelOffsetY * uiScale + sink));

        // Room for the outline and shadow to reach past the artwork without being cut off again.
        float pad  = (OutlinePx + ShadowPx) * uiScale + 2f;
        int   span = (int)MathF.Ceiling(side + pad * 2f);

        if (!BeginHud("##tc_dig_label", min - new Vector2(pad, pad), span, span)) return;

        var drawList = ImGui.GetWindowDrawList();
        var size     = new Vector2(side, side);
        float outline = OutlinePx * uiScale;

        // Each word takes the colour its OWN state implies, not the dial's current one — a DIG!
        // fading out as the player walks away should keep the dig yellow while it goes rather than
        // sliding back down the proximity ramp with the dial behind it.
        if (clueAlpha > 0.002f && _labelClue != null)
            DrawEmblem(drawList, _labelClue, min, size,
                       PressedAccent(Ramp(closeness), press), clueAlpha, outline, GradientBands);

        if (digAlpha > 0.002f && _labelDig != null)
            DrawEmblem(drawList, _labelDig, min, size,
                       PressedAccent(Rgb(DigCol), press), digAlpha, outline, GradientBands);

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

    private static PColor RampColor(float closeness) => FromVec(Ramp(closeness));

    /// <summary>A 0..1 float colour as a PanacheUI byte colour.</summary>
    private static PColor FromVec(Vector3 rgb) =>
        new((byte)MathF.Round(Math.Clamp(rgb.X, 0f, 1f) * 255f),
            (byte)MathF.Round(Math.Clamp(rgb.Y, 0f, 1f) * 255f),
            (byte)MathF.Round(Math.Clamp(rgb.Z, 0f, 1f) * 255f));

    // ── the vertical gradient ────────────────────────────────────────────────

    /// <summary>
    /// How much darker the bottom of a tinted image is than its top, as a multiplier, and how many
    /// horizontal bands the gradient is built from.
    ///
    /// <para>0.70 is a gentle drop — enough to read as lit from above, not enough to look like two
    /// colours. It matches the fall the supplied artwork already has painted into it, so a tinted
    /// element and an untinted one sit together instead of one looking flat beside the other.</para>
    /// </summary>
    private const float GradientDrop  = 0.70f;
    private const int   GradientBands = 24;

    // ── DO NOT ADD AN INNER EDGE HIGHLIGHT HERE. IT HAS FAILED TWICE. ────────
    //
    // The reference artwork has a white-to-gold band just inside its black outline, and reproducing
    // it from a white silhouette on a draw list was attempted two ways. Both shipped and both were
    // immediately, visibly wrong:
    //
    //   1. SCALE (0.84.42.41). Draw the fill as a slightly smaller copy so the layer beneath shows
    //      as an inner band. Only correct for artwork centred in its canvas — scaling pulls
    //      everything toward the CANVAS centre, so an off-centre letter slides inward as it shrinks
    //      and the "band" is fat outside and absent inside. On TRAIL START! it read as a second,
    //      smaller copy of the word laid over the first, because that is what it was.
    //
    //   2. STAMP (0.84.42.42). Ring the shape with displaced copies in the rim colours. Geometrically
    //      sound, and much worse to look at: twelve full-size copies at an offset build a thick spiky
    //      halo whose outline is the union of twelve shifted silhouettes, so every serif grows a
    //      fringe and small artwork is swallowed whole. The CLUE emblem became unrecognisable.
    //
    // The technique both attempts needed is EROSION — shrinking a shape's coverage inward from its
    // own edge — and a draw list cannot do it. Blending only ever adds coverage; there is no
    // subtract, no stencil, and no per-pixel access. It would need the effect baked into the PNG by
    // whoever draws it, or a real shader pass. Neither is a thing to reach for from here.
    //
    // What follows is the treatment that works: a thin dark outline, and one gradient fill.

    /// <summary>
    /// Vertical falloff for the artwork fill, as a live knob rather than a constant — this is the
    /// one part of the "make it look lit" attempt that is safe, because it changes colour only and
    /// touches no geometry.
    /// </summary>
    private static float IconDrop => Math.Clamp(DigTuning.IconGradientDrop, 0.2f, 1f);

    /// <summary>
    /// Draws an image tinted with a vertical gradient: <paramref name="rgb"/> at the top falling to
    /// <see cref="GradientDrop"/> of it at the bottom.
    ///
    /// <para><b>Banded, for the same reason the in-world gradient walls are.</b> A draw list's
    /// <c>AddImage</c> takes ONE tint for the whole quad — there is no per-vertex colour on that
    /// path — so the gradient is built by slicing the image into horizontal strips and giving each
    /// its own flat tint. Each strip's UV range is exactly its share of the source, so the artwork
    /// is uncut; only the colour steps. The per-vertex route via <c>PrimReserve</c>/<c>PrimWriteVtx</c>
    /// exists and would give a true two-triangle gradient, and is deliberately not taken here for
    /// the reason recorded in CLAUDE.md: a wrong index count corrupts the shared draw list, which is
    /// a graphical crash rather than a wrong colour.</para>
    ///
    /// <para><b>Band edges are rounded to whole pixels and the UVs derived from the rounded
    /// positions</b>, never the other way round. Fractional abutting quads leave sub-pixel seams —
    /// <c>AddImage</c> has no antialiasing to hide them — and a row of hairlines across a piece of
    /// lettering is far more visible than the banding this is trading against.</para>
    /// </summary>
    private static void GradientImage(ImDrawListPtr drawList, ImTextureID tex,
                                      Vector2 min, Vector2 max, Vector3 rgb, float alpha,
                                      int bands = GradientBands, float drop = GradientDrop)
        // A drop of 1 is a flat tint. Switching a gradient off goes through here rather than round
        // it, so "no gradient" cannot drift from "gradient" in any other respect.
        => GradientImage(drawList, tex, min, max, rgb, rgb * drop, alpha, bands);

    /// <summary>
    /// The two-colour form. <paramref name="top"/> and <paramref name="bottom"/> need not be shades
    /// of one another — the emblem's edge layer runs white to the accent, which is a hue change and
    /// not a brightness one.
    /// </summary>
    private static void GradientImage(ImDrawListPtr drawList, ImTextureID tex,
                                      Vector2 min, Vector2 max,
                                      Vector3 top, Vector3 bottom, float alpha,
                                      int bands = GradientBands)
    {
        if (alpha <= 0.002f) return;

        float height = max.Y - min.Y;
        if (height <= 0f || max.X <= min.X) return;

        if (bands < 1) bands = 1;

        var rgb = top;

        float yPrev = MathF.Round(min.Y);

        for (int i = 0; i < bands; i++)
        {
            float yNext = i == bands - 1
                ? MathF.Round(max.Y)
                : MathF.Round(min.Y + height * (i + 1) / bands);

            // A band that rounded away to nothing is skipped rather than drawn zero-high — its
            // share of the gradient is simply covered by its neighbours.
            if (yNext <= yPrev) continue;

            float v0 = (yPrev - min.Y) / height;
            float v1 = (yNext - min.Y) / height;

            // The colour at the band's own MIDPOINT, so the visible steps straddle the true ramp
            // instead of all sitting on one side of it.
            var col = Tint(Vector3.Lerp(rgb, bottom, (v0 + v1) * 0.5f), alpha);

            drawList.AddImage(tex,
                              new Vector2(min.X, yPrev), new Vector2(max.X, yNext),
                              new Vector2(0f, v0),       new Vector2(1f, v1),
                              col);

            yPrev = yNext;
        }
    }

    /// <summary>
    /// Draws one piece of white-on-transparent artwork: a thin dark outline, then the shape at true
    /// size on a vertical gradient. That is all, and the block above this one is why.
    ///
    /// <para>The outline is stamped, which is safe precisely because it is THIN — at a pixel or two
    /// the union of eight displaced copies is indistinguishable from the silhouette's own edge. The
    /// same trick at rim width is what destroyed the artwork, so treat the offset here as something
    /// that must stay small rather than as a dial to turn up.</para>
    ///
    /// <para><b>The gradient spans the WHOLE image, not each letter.</b> That falls out of how
    /// <see cref="GradientImage"/> works — bands are slices of the destination rectangle — and it is
    /// the point: per-letter shading would make each character read as its own object instead of the
    /// lettering reading as one lit surface.</para>
    /// </summary>
    private static void DrawEmblem(ImDrawListPtr drawList, IDalamudTextureWrap tex,
                                   Vector2 origin, Vector2 size, Vector3 rgb, float alpha,
                                   float outline, int bands)
        // One colour plus the shared falloff. The two-colour form below is what the trail banners
        // use, since their top and bottom are picked independently.
        => DrawEmblem(drawList, tex, origin, size, rgb, rgb * IconDrop, alpha, outline, bands);

    private static void DrawEmblem(ImDrawListPtr drawList, IDalamudTextureWrap tex,
                                   Vector2 origin, Vector2 size, Vector3 top, Vector3 bottom,
                                   float alpha, float outline, int bands)
    {
        if (alpha <= 0.002f) return;

        // OUTER, always: the shape goes down in dark FIRST and the artwork covers it, so the dark
        // survives only where it reaches past the art. The artwork itself is drawn at its true size
        // and position and is never inset or shrunk — see DigTuning.IconOutline for the two ways a
        // wide outline can still look like it is eating the image, and why narrow is the answer.
        if (DigTuning.IconOutline && outline > 0f)
            Stamp(drawList, tex, origin, size, outline,
                  Tint(Ink, Math.Clamp(DigTuning.IconOutlineAlpha, 0f, 1f) * alpha));

        GradientImage(drawList, tex.Handle, origin, origin + size, top, bottom, alpha, bands);
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
