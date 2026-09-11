#if DEV_BUILD
using System;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. Every distance the three dig tests measure against, in one place, editable
/// live from <see cref="DigTestsWindow"/>.
///
/// <para><b>Why these are not consts.</b> Sansflaire asked for a debug way to define the ranges, and the
/// reason is that none of these numbers can be reasoned out at a desk — "close enough to dig" is a
/// feel, and the only way to find it is to stand in a field and try it. A const would mean a
/// rebuild per guess, and a rebuild hot-reloads the plugin, which is a thing to do to somebody's
/// game as rarely as possible.</para>
///
/// <para><b>They persist to their own file, not to <c>Configuration</c>.</b> These are test knobs
/// with no public-build meaning, and putting them in the config would push them into every player's
/// settings JSON and into the migration path forever. <c>dig-tuning.json</c> can simply be deleted
/// to get the defaults back.</para>
/// </summary>
internal static class DigTuning
{
    // ── Test 1: Sense Hunt ───────────────────────────────────────────────────

    /// <summary>Outer edge of the proximity readout. Past this the player has only Sense.</summary>
    public static float HuntNearby = 40f;

    /// <summary>Red becomes yellow here.</summary>
    public static float HuntWarm = 20f;

    /// <summary>Yellow becomes green here.</summary>
    public static float HuntHot = 10f;

    /// <summary>Green becomes DIG here — and the only range a dig can find the spot.</summary>
    public static float HuntDig = 5f;

    /// <summary>Closest and furthest the spot may be buried from where the hunt was started.</summary>
    public static float HuntMinPlacement = 60f;
    public static float HuntMaxPlacement = 220f;

    // ── Test 2: Area Surveillance ────────────────────────────────────────────

    /// <summary>
    /// Side length of the drawn square site, in yalms. 50, not 70 — Sansflaire asked for roughly half
    /// the ground to search, and halving an AREA means dividing the side by √2, not by two.
    /// 70 → 49.5, taken as 50.
    /// </summary>
    public static float SiteSize = 50f;

    /// <summary>How many pieces are buried inside it. Collect them all to get the relic.</summary>
    public static int SitePieces = 5;

    /// <summary>How close a dig must be to a piece to turn it up.</summary>
    public static float SitePieceRadius = 4f;

    /// <summary>Minimum gap between two buried pieces, so no dig can turn up two at once.</summary>
    public static float SitePieceSpacing = 12f;

    /// <summary>
    /// How much the ground may rise or fall across a piece's dig radius, in yalms.
    ///
    /// <para>This is not only cosmetic. A spot straddling a wall or a ledge has half its dig radius
    /// somewhere the player cannot stand, so the reachable area is smaller than the marker claims —
    /// and the marker itself smears vertically up the face, which is what made it visible. Pieces
    /// are refused at placement when the surrounding ground exceeds this, and any that slip through
    /// have the offending part of their circle clipped rather than drawn.</para>
    /// </summary>
    public static float SitePieceMaxDrop = 2f;

    /// <summary>
    /// The largest height jump allowed between two ground samples half a yalm apart, in yalms.
    ///
    /// <para><b>This is what tells a wall from a hill, and total height cannot.</b> A 2-yalm rise
    /// across a 4-yalm radius is a 26° slope — perfectly walkable and fine to dig on. A 1-yalm
    /// garden wall is a smaller total rise and completely unusable. The difference is not how far
    /// the ground moves but how suddenly, so this measures the step between neighbouring samples
    /// rather than the spread across the circle.</para>
    ///
    /// <para>0.4 over a 0.5 spacing is about 39°, so ordinary terrain passes and anything with an
    /// actual edge does not.</para>
    /// </summary>
    public static float SitePieceMaxStep = 0.4f;

    /// <summary>
    /// How tall the drawn walls are, in yalms. <b>Nothing to do with the volume's own height</b> —
    /// the site box is deliberately tall so its containment test survives sloped ground, and drawing
    /// the wall over that height put the gradient's opaque base metres underground.
    /// </summary>
    public static float SiteWallHeight = 1.5f;

    /// <summary>
    /// Grid cells across the site, both axes — the site is square, so one number does both.
    /// Also sets how finely the walls follow the ground, since the wall footprint IS the grid's
    /// outer edge.
    /// </summary>
    public static int SiteGridCells = 12;

    /// <summary>
    /// Colour of the walls and the floor grid. A picker rather than a constant because the right
    /// colour depends on the ground it is drawn over, and Eorzea's ground is not one colour.
    /// </summary>
    public static Vector3 SiteColor = DefaultSiteColor;

    public static readonly Vector3 DefaultSiteColor = new(0.89f, 0.70f, 0.25f);

    /// <summary>
    /// Draw the striped floor grid. Off leaves the walls and the markers, which is the quickest way
    /// to tell whether the grid is helping to read the site or just cluttering it.
    /// </summary>
    public static bool SiteShowGrid = true;

    /// <summary>
    /// Debug-draw the buried pieces that have NOT been found yet, at their exact dig radius.
    ///
    /// <para>This is the answer key — with it on, the test is not a test. It exists to check that
    /// placement, spacing and the dig radius are what the sliders say, which is impossible to
    /// verify by digging blindly: a miss and a mis-tuned radius look identical from inside the
    /// game. Turn it off to actually play.</para>
    /// </summary>
    public static bool SiteRevealPieces = true;

    /// <summary>
    /// Colour for the revealed piece markers. Kept distinct from both the site colour and the green
    /// used for pieces already recovered, so all three can be on screen without ambiguity.
    /// </summary>
    public static Vector3 SiteDebugColor = DefaultDebugColor;

    public static readonly Vector3 DefaultDebugColor = new(0.30f, 0.85f, 0.95f);

    // ── Test 3: Clue Trail ───────────────────────────────────────────────────

    // TrailStops and TrailSpacing were removed in 0.84.42.14 along with the trail generator. The
    // trail is authored now, so how many stops there are and how far apart they sit are decisions
    // made by walking to them, not numbers. Old files may still carry the keys; they are ignored.

    /// <summary>
    /// Seconds for the radar to fade fully in on entering range, or fully out on leaving it.
    ///
    /// <para>It is a ramp rate, not a duration: a partial fade takes proportionally less, so
    /// stepping in and out of range repeatedly makes the ring drift up and down rather than
    /// restarting.</para>
    /// </summary>
    public static float RadarFadeSeconds = 2.5f;

    /// <summary>How close a dig must be to a trail spot to turn up the next clue.</summary>
    public static float TrailDig = 4f;

    /// <summary>
    /// The radar appears at this distance and pulses faster the closer you get. It must always be
    /// larger than <see cref="TrailDig"/> — a radar that only shows up once you are already in
    /// range tells you nothing you did not already know.
    /// </summary>
    public static float TrailRadar = 30f;


    // ── the shared dig HUD ───────────────────────────────────────────────────

    /// <summary>
    /// How far DOWN the whole dial-and-clue assembly sits from where it would otherwise be, in
    /// logical pixels. The dial and the clue move together, so this never changes the gap between
    /// them — only where the pair as a whole lands.
    /// </summary>
    /// <summary>
    /// Gap between the bottom of the dial and the top of the clue line, in logical pixels.
    ///
    /// <para><b>These two are sliders for the same reason every distance here is.</b> The game
    /// prints its own location banner in the space between the dial and the clue, and where that
    /// banner lands is not something this plugin can measure — it belongs to the game's HUD layout,
    /// moves with the player's own HUD configuration, and is not exposed anywhere we can read. So
    /// the only way to seat our two elements around it is to look at the screen and nudge, which is
    /// precisely the case a const cannot serve.</para>
    ///
    /// <para>The defaults are set to drop the pair slightly and open the gap wide enough for a line
    /// of the game's text to sit centred between them, which is the arrangement Sansflaire asked for
    /// (2026-09-10). Treat them as a starting point, not an answer.</para>
    /// </summary>
    public static float HudDropPx    = DefaultHudDropPx;
    public static float HudClueGapPx = DefaultHudClueGapPx;

    /// <summary>
    /// 0, not 14. The first pass dropped the pair AND opened the gap, and only the gap was needed —
    /// widening the space is what let the game's banner sit between them, so the drop on top of it
    /// just pushed the whole assembly too low. Kept as a knob at zero rather than removed: it is
    /// the control for exactly this, and the next HUD layout it has to coexist with will want it.
    /// </summary>
    public const float DefaultHudDropPx    = 0f;
    public const float DefaultHudClueGapPx = 46f;

    /// <summary>
    /// How the clue line is painted. Face colour, rim colour, and whether each layer is drawn at
    /// all.
    ///
    /// <para><b>The rim defaults to near-black, not white.</b> A white rim was tried and is close to
    /// useless: its whole job is to separate the face from the ground, and the grounds that defeat
    /// yellow type — snow, frost, pale stone, lit grass — are the exact grounds a white rim
    /// disappears into. A dark rim works against every bright surface AND every dark one, because
    /// the face it is separating is light. That asymmetry is why outlined game text is nearly always
    /// dark-on-light rather than the reverse.</para>
    ///
    /// <para>All of it is adjustable because "readable" depends on ground this plugin cannot
    /// predict, and Eorzea's ground is not one colour — same reasoning as the site colour picker.
    /// </para>
    /// </summary>
    public static Vector3 ClueFaceColor    = DefaultClueFace;
    public static Vector3 ClueOutlineColor = DefaultClueOutline;

    public static bool ClueOutline  = true;
    public static bool ClueShadow   = true;

    /// <summary>
    /// The downward colour falloff on the clue. On by default because it matches the artwork, but a
    /// toggle because it necessarily dims the BOTTOM of the type — which is the part that has to
    /// survive the worst ground.
    /// </summary>
    public static bool ClueGradient = true;

    public static float ClueOutlineWidth = 2.5f;
    public static float ClueShadowOffset = 4f;
    public static float ClueFontSize     = 22f;

    public static readonly Vector3 DefaultClueFace    = new(1.00f, 0.90f, 0.42f);
    public static readonly Vector3 DefaultClueOutline = new(0.05f, 0.04f, 0.08f);

    /// <summary>
    /// The burst of light behind the dial once a dig would land — see
    /// <c>DigHuntOverlay.DrawRays</c>.
    ///
    /// <para><b>Colour is a knob and needs to be.</b> ImGui blends normally rather than additively,
    /// so white light drawn over snow is white on white and simply is not there. A real light effect
    /// would add rather than blend and would blow out against anything; this cannot, so on pale
    /// ground the rays want a colour with somewhere to go — amber against snow, white against
    /// stone or night.</para>
    /// </summary>
    public static bool    RaysEnabled = true;
    public static int     RayCount    = 18;
    public static float   RayReach    = 130f;
    public static float   RayOpacity  = 0.80f;
    public static float   RaySpeed    = 0.18f;
    public static Vector3 RayColor    = DefaultRayColor;

    /// <summary>
    /// How unlike each other the beams are. Width decides how much the angular slices differ,
    /// speed how much their breathing rates differ, reach how much shorter some stay than others.
    ///
    /// <para><b>Beams always TILE the full turn regardless of the width spread</b> — the widths are
    /// weights normalised to 360°, not independent sizes with space between them. Gaps were what
    /// made the first attempt read as a cog rather than as light.</para>
    /// </summary>
    public static float RayWidthVariance = 1.0f;
    public static float RaySpeedVariance = 0.55f;
    public static float RayReachVariance = 0.45f;

    /// <summary>
    /// How short a beam pulls back to at its trough, as a share of its own full reach — 0 means it
    /// retracts to nothing, 1 means it never moves.
    /// </summary>
    public static float RayMinLength = 0.35f;

    /// <summary>
    /// Shape of the fade along a beam. 1 is a straight ramp to nothing; higher concentrates the
    /// light near the icon and lets the outer end trail away sooner.
    /// </summary>
    public static float RayFalloff = 1.6f;

    /// <summary>
    /// How far each beam's left and right edges fade out. 0 is a hard-edged wedge; 1 fades to
    /// nothing at the edge.
    ///
    /// <para><b>It also widens the beams, and it has to.</b> Beams tile the full turn exactly, so
    /// fading each edge to nothing on its own slot would leave a dark seam wherever two meet — the
    /// same gap problem in reverse. Raising this therefore spreads each beam past its slot by the
    /// same measure, so a beam's faded edge lies over its neighbour's instead of beside it and the
    /// boundary stays lit. At 0 there is no spread, because there is nothing to cover.</para>
    /// </summary>
    public static float RayEdgeFade = 0.75f;

    /// <summary>
    /// How dark the BOTTOM of a piece of dig artwork goes, as a multiplier on its colour. 1 is a
    /// flat tint; lower is a stronger top-to-bottom fall.
    ///
    /// <para>The default is the value that was on screen before any of the shading work, so this
    /// slider starts by changing nothing. It exists because a stronger fall is the one part of
    /// "make the artwork look lit" that is safe — it touches colour only. The other parts, an inner
    /// edge highlight by scaling or by stamping, both wrecked the artwork; see the block at the top
    /// of <c>DigHuntOverlay</c>'s gradient section before attempting either again.</para>
    /// </summary>
    public static float IconGradientDrop = 0.70f;

    /// <summary>
    /// Where the CLUE! / DIG! word sits relative to the dial, and how big it is drawn.
    ///
    /// <para>The word is authored into the top of the SAME canvas as the dial, so drawing it at the
    /// dial's own rect is where the artist put it. These three are the deliberate nudge off that —
    /// the default Y lifts it clear of the rim, which is the one adjustment it always needed.</para>
    ///
    /// <para>Scale is applied about the dial's CENTRE, so growing the word does not also walk it
    /// sideways and leave the offsets meaning something different at every size.</para>
    /// </summary>
    public static float LabelOffsetX = 0f;
    public static float LabelOffsetY = -26f;
    public static float LabelScale   = 1f;

    /// <summary>
    /// The TRAIL START! / TRAIL END! banners' vertical gradient, picked at both ends rather than
    /// derived from one colour and a falloff.
    ///
    /// <para>Two pickers because a banner is the largest thing this HUD ever draws and the one most
    /// worth colouring deliberately — the derived form can only ever make the bottom a darker copy
    /// of the top, which rules out the warm-to-deep shifts real title art uses. The defaults are the
    /// gold the artwork was drawn against and that same gold at the shared falloff, so this starts
    /// exactly where it was.</para>
    /// </summary>
    public static Vector3 BannerTopColor    = DefaultBannerTop;
    public static Vector3 BannerBottomColor = DefaultBannerBottom;

    public static readonly Vector3 DefaultBannerTop    = new(1.00f, 0.847f, 0.302f);
    public static readonly Vector3 DefaultBannerBottom = new(0.70f, 0.593f, 0.211f);

    public static readonly Vector3 DefaultRayColor = new(1f, 1f, 1f);

    // ── shared ───────────────────────────────────────────────────────────────

    /// <summary>
    /// How far above or below the player a buried spot may sit. This is the whole "could you
    /// actually walk there" heuristic — it rejects clifftops, rooftops, and the floor of the ravine
    /// you happen to be standing over.
    /// </summary>
    public static float MaxRise = 25f;

    /// <summary>
    /// How long the dig animation runs before it ends itself and the shovel comes off, in seconds.
    /// <b>0 means no cap</b> — run until the game cancels it, which is the shipped default.
    ///
    /// <para>This is the one tuning value that is not a distance, and the one that drives something
    /// in the PUBLIC build: it is applied to <see cref="PropService.HoldMilliseconds"/>, which ships
    /// because challenge content plays the dig. The knob is dev-only; the behaviour it sets is not.
    /// </para>
    ///
    /// <para><b>The default is derived from <see cref="PropService.DefaultHoldMilliseconds"/>, never
    /// written out again here.</b> Two copies of the shipped default is two numbers that can drift,
    /// and the drift would be invisible: the slider would simply open on a value that was not what
    /// the plugin actually does. A saved <c>dig-tuning.json</c> still wins over the default — that
    /// is a deliberate override, and "Reset ALL tuning" puts it back.</para>
    /// </summary>
    public static float DigHoldSeconds = PropService.DefaultHoldMilliseconds / 1000f;

    /// <summary>
    /// Dig animation playback speed. 1 = untouched. Drives
    /// <see cref="PropService.PlaybackSpeed"/>, which ships.
    /// </summary>
    public static float DigSpeed = 1f;

    /// <summary>
    /// Which speed lever to use — see <see cref="PropService.SpeedMode"/>. Defaults to
    /// <c>SlotFunction</c>: measured 2026-09-09, both per-slot levers work alone, so the
    /// character-wide <c>OverallSpeed</c> is not needed and should not be written.
    /// </summary>
    public static int DigSpeedMethod = (int)PropService.SpeedMode.SlotFunction;

    /// <summary>
    /// Pushes the values that live somewhere else into their real home. Called after
    /// <see cref="Load"/> and whenever the lab changes one — a tuning value that is only read at
    /// startup would look like it had silently stopped working the first time it was edited.
    /// </summary>
    public static void Apply()
    {
        try
        {
            Plugin.Props.HoldMilliseconds = (int)MathF.Round(DigHoldSeconds * 1000f);
            Plugin.Props.PlaybackSpeed    = Math.Clamp(DigSpeed, PropService.MinSpeed, PropService.MaxSpeed);
            Plugin.Props.SpeedMethod      = (PropService.SpeedMode)Math.Clamp(DigSpeedMethod, 0, 3);
        }
        catch (Exception ex) { Diag.Error($"[Dig] applying tuning failed: {ex.Message}"); }
    }

    // ── persistence ──────────────────────────────────────────────────────────

    /// <summary>
    /// Bumped when a stored value's MEANING changes, not when a field is added. Version 2 exists
    /// because <c>DigHoldSeconds</c> shipped for one version defaulting to 0 ("no cap"), so any
    /// save written by that version recorded a 0 the user never chose — and on load that 0 would
    /// have silently overridden the 9s default with the exact behaviour it replaced. A missing or
    /// older version therefore takes the shipped default rather than the stored number.
    /// </summary>
    /// <summary>
    /// Version 3: the site defaults changed meaningfully (side 70 → 50, wall height 6 → 1.5), so a
    /// file written before it holds the OLD defaults and would silently reinstate them.
    ///
    /// <para><b>Each migration is keyed to its own version, never to <c>CurrentVersion</c>.</b>
    /// Gating on "older than current" would make every future bump re-run every past migration and
    /// overrule values the user chose deliberately in between.</para>
    /// </summary>
    private const int CurrentVersion = 6;

    private sealed class Dto
    {
        public int Version { get; set; }

        public float HuntNearby { get; set; }
        public float HuntWarm { get; set; }
        public float HuntHot { get; set; }
        public float HuntDig { get; set; }
        public float HuntMinPlacement { get; set; }
        public float HuntMaxPlacement { get; set; }
        public float SiteSize { get; set; }
        public int   SitePieces { get; set; }
        public float SitePieceRadius { get; set; }
        public float SitePieceSpacing { get; set; }
        public float SiteWallHeight { get; set; }
        public float SitePieceMaxDrop { get; set; }
        public float SitePieceMaxStep { get; set; }
        public int   SiteGridCells { get; set; }
        public float SiteColorR { get; set; }
        public float SiteColorG { get; set; }
        public float SiteColorB { get; set; }

        /// <summary>Nullable so "absent" is distinguishable from a deliberate false.</summary>
        public bool? SiteRevealPieces { get; set; }
        public bool? SiteShowGrid { get; set; }

        public float SiteDebugColorR { get; set; }
        public float SiteDebugColorG { get; set; }
        public float SiteDebugColorB { get; set; }
        // Kept on the DTO so an older file still deserialises; nothing reads them any more.
        public int   TrailStops { get; set; }
        public float TrailDig { get; set; }
        public float TrailRadar { get; set; }
        public float RadarFadeSeconds { get; set; }
        public float TrailSpacing { get; set; }
        public float MaxRise { get; set; }
        public float DigHoldSeconds { get; set; }
        public float DigSpeed { get; set; }
        public int?  DigSpeedMethod { get; set; }

        /// <summary>
        /// Nullable because 0 is a legitimate value for both — "no drop" and "no gap" are things
        /// somebody might genuinely want, so absent has to be distinguishable from chosen-zero.
        /// </summary>
        public float? HudDropPx { get; set; }
        public float? HudClueGapPx { get; set; }

        /// <summary>
        /// Marks the whole clue-style block as present.
        ///
        /// <para><b>The all-zero-means-absent trick the site colours use cannot work here.</b> The
        /// rim's default IS very nearly black, so "did somebody choose this, or is it a missing
        /// field?" has no answer from the value alone. One explicit flag settles it for every field
        /// in the block at once.</para>
        /// </summary>
        public bool? ClueStyleSaved { get; set; }

        public float ClueFaceR { get; set; }
        public float ClueFaceG { get; set; }
        public float ClueFaceB { get; set; }
        public float ClueOutlineR { get; set; }
        public float ClueOutlineG { get; set; }
        public float ClueOutlineB { get; set; }
        public bool  ClueOutline { get; set; }
        public bool  ClueShadow { get; set; }
        public bool  ClueGradient { get; set; }
        public float ClueOutlineWidth { get; set; }
        public float ClueShadowOffset { get; set; }
        public float ClueFontSize { get; set; }

        /// <summary>Same present-marker trick as <see cref="ClueStyleSaved"/>, for the ray block.</summary>
        public bool? RaysSaved { get; set; }

        public bool  RaysEnabled { get; set; }
        public int   RayCount { get; set; }
        public float RayReach { get; set; }
        public float RayOpacity { get; set; }
        public float RaySpeed { get; set; }
        public float RayColorR { get; set; }
        public float RayColorG { get; set; }
        public float RayColorB { get; set; }
        public float RayWidthVariance { get; set; }
        public float RaySpeedVariance { get; set; }
        public float RayReachVariance { get; set; }
        public float RayMinLength { get; set; }
        public float RayFalloff { get; set; }
        public float RayEdgeFade { get; set; }
        public float IconGradientDrop { get; set; }
        public float LabelOffsetX { get; set; }
        public float LabelOffsetY { get; set; }
        public float LabelScale { get; set; }
        public float BannerTopR { get; set; }
        public float BannerTopG { get; set; }
        public float BannerTopB { get; set; }
        public float BannerBottomR { get; set; }
        public float BannerBottomG { get; set; }
        public float BannerBottomB { get; set; }
    }

    private static string Path =>
        System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "dig-tuning.json");

    /// <summary>Restores the shipped defaults. The window offers this per test and for everything.</summary>
    public static void ResetHunt()
    {
        HuntNearby = 40f; HuntWarm = 20f; HuntHot = 10f; HuntDig = 5f;
        HuntMinPlacement = 60f; HuntMaxPlacement = 220f;
    }

    public static void ResetSite()
    {
        SiteSize = 50f; SitePieces = 5; SitePieceRadius = 4f; SitePieceSpacing = 12f;
        SiteWallHeight = 1.5f; SiteGridCells = 12; SiteColor = DefaultSiteColor;
        SitePieceMaxDrop = 2f; SitePieceMaxStep = 0.4f;
        SiteRevealPieces = true; SiteDebugColor = DefaultDebugColor; SiteShowGrid = true;
    }

    public static void ResetTrail()
    {
        TrailDig = 4f; TrailRadar = 30f; RadarFadeSeconds = 2.5f;
    }

    public static void ResetHud()
    {
        HudDropPx = DefaultHudDropPx; HudClueGapPx = DefaultHudClueGapPx;
    }

    public static void ResetClueStyle()
    {
        ClueFaceColor    = DefaultClueFace;
        ClueOutlineColor = DefaultClueOutline;
        ClueOutline      = true;
        ClueShadow       = true;
        ClueGradient     = true;
        ClueOutlineWidth = 2.5f;
        ClueShadowOffset = 4f;
        ClueFontSize     = 22f;
    }

    public static void ResetRays()
    {
        RaysEnabled = true; RayCount = 18; RayReach = 130f;
        RayOpacity = 0.80f; RaySpeed = 0.18f; RayColor = DefaultRayColor;
        RayWidthVariance = 1.0f; RaySpeedVariance = 0.55f; RayReachVariance = 0.45f;
        RayMinLength = 0.35f; RayFalloff = 1.6f; RayEdgeFade = 0.75f;
        IconGradientDrop = 0.70f;
        LabelOffsetX = 0f; LabelOffsetY = -26f; LabelScale = 1f;
        BannerTopColor = DefaultBannerTop; BannerBottomColor = DefaultBannerBottom;
    }

    public static void ResetAll()
    {
        ResetHunt(); ResetSite(); ResetTrail(); ResetHud(); ResetClueStyle(); ResetRays();
        MaxRise        = 25f;
        DigHoldSeconds = PropService.DefaultHoldMilliseconds / 1000f;
        DigSpeed       = 1f;
        DigSpeedMethod = (int)PropService.SpeedMode.SlotFunction;
        Apply();
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(Path)) return;

            var d = JsonSerializer.Deserialize<Dto>(File.ReadAllText(Path));
            if (d == null) return;

            // Guard every value. A hand-edited or half-written file must not be able to put the
            // tests into a state where nothing can ever be found — a zero dig radius is
            // indistinguishable from a broken detector once you are standing in the field.
            HuntNearby       = Pos(d.HuntNearby,       40f);
            HuntWarm         = Pos(d.HuntWarm,         20f);
            HuntHot          = Pos(d.HuntHot,          10f);
            HuntDig          = Pos(d.HuntDig,           5f);
            HuntMinPlacement = Pos(d.HuntMinPlacement, 60f);
            HuntMaxPlacement = Pos(d.HuntMaxPlacement, 220f);
            SiteSize         = Pos(d.SiteSize,         70f);
            SitePieces       = d.SitePieces  > 0 ? Math.Min(d.SitePieces, 20) : 5;
            SitePieceRadius  = Pos(d.SitePieceRadius,   4f);
            SitePieceSpacing = Pos(d.SitePieceSpacing, 12f);
            SiteWallHeight   = Pos(d.SiteWallHeight,    6f);
            SitePieceMaxDrop = Pos(d.SitePieceMaxDrop,  2f);
            SitePieceMaxStep = Pos(d.SitePieceMaxStep, 0.4f);
            SiteGridCells    = d.SiteGridCells > 0 ? Math.Clamp(d.SiteGridCells, 2, 40) : 12;

            // All-zero means the field was absent, not that somebody picked black — a colour that
            // would render the whole site invisible is never what a saved file meant.
            SiteColor = d.SiteColorR <= 0f && d.SiteColorG <= 0f && d.SiteColorB <= 0f
                            ? DefaultSiteColor
                            : new Vector3(Math.Clamp(d.SiteColorR, 0f, 1f),
                                          Math.Clamp(d.SiteColorG, 0f, 1f),
                                          Math.Clamp(d.SiteColorB, 0f, 1f));

            // Null means the field was absent; false means somebody switched it off on purpose.
            SiteRevealPieces = d.SiteRevealPieces ?? true;
            SiteShowGrid     = d.SiteShowGrid     ?? true;

            SiteDebugColor = d.SiteDebugColorR <= 0f && d.SiteDebugColorG <= 0f && d.SiteDebugColorB <= 0f
                                 ? DefaultDebugColor
                                 : new Vector3(Math.Clamp(d.SiteDebugColorR, 0f, 1f),
                                               Math.Clamp(d.SiteDebugColorG, 0f, 1f),
                                               Math.Clamp(d.SiteDebugColorB, 0f, 1f));
            TrailDig         = Pos(d.TrailDig,          4f);
            TrailRadar       = Pos(d.TrailRadar,       30f);
            RadarFadeSeconds = Pos(d.RadarFadeSeconds, 2.5f);
            MaxRise          = Pos(d.MaxRise,          25f);

            // Clamped, not Pos-guarded: 0 is meaningful for both, and a negative drop (lifting the
            // pair) is a legitimate thing to want. Only a missing or non-finite value defaults.
            HudDropPx = d.HudDropPx is { } hd && float.IsFinite(hd)
                            ? Math.Clamp(hd, -200f, 400f) : DefaultHudDropPx;

            HudClueGapPx = d.HudClueGapPx is { } hg && float.IsFinite(hg)
                            ? Math.Clamp(hg, 0f, 400f) : DefaultHudClueGapPx;

            // One flag for the whole block — see Dto.ClueStyleSaved for why the all-zero test the
            // site colours use cannot work for a colour whose default is nearly black.
            if (d.ClueStyleSaved == true)
            {
                ClueFaceColor    = Rgb(d.ClueFaceR,    d.ClueFaceG,    d.ClueFaceB);
                ClueOutlineColor = Rgb(d.ClueOutlineR, d.ClueOutlineG, d.ClueOutlineB);
                ClueOutline      = d.ClueOutline;
                ClueShadow       = d.ClueShadow;
                ClueGradient     = d.ClueGradient;
                ClueOutlineWidth = Clamp(d.ClueOutlineWidth, 0f, 8f,  2.5f);
                ClueShadowOffset = Clamp(d.ClueShadowOffset, 0f, 12f, 4f);
                ClueFontSize     = Clamp(d.ClueFontSize,    10f, 48f, 22f);
            }
            else ResetClueStyle();

            if (d.RaysSaved == true)
            {
                RaysEnabled = d.RaysEnabled;
                RayCount    = d.RayCount > 0 ? Math.Clamp(d.RayCount, 3, 48) : 18;
                RayReach    = Clamp(d.RayReach,   10f, 500f, 130f);
                RayOpacity  = Clamp(d.RayOpacity,  0f,   1f, 0.80f);
                RaySpeed    = Clamp(d.RaySpeed, 0.02f,   3f, 0.18f);
                RayColor    = Rgb(d.RayColorR, d.RayColorG, d.RayColorB);

                RayWidthVariance = Clamp(d.RayWidthVariance, 0f, 3f, 1.0f);
                RaySpeedVariance = Clamp(d.RaySpeedVariance, 0f, 1f, 0.55f);
                RayReachVariance = Clamp(d.RayReachVariance, 0f, 1f, 0.45f);
                RayMinLength     = Clamp(d.RayMinLength,     0f, 1f, 0.35f);
                RayFalloff       = Clamp(d.RayFalloff,     0.2f, 5f, 1.6f);
                RayEdgeFade      = Clamp(d.RayEdgeFade,      0f, 1f, 0.75f);
                IconGradientDrop = Clamp(d.IconGradientDrop, 0.2f, 1f, 0.70f);
                LabelOffsetX     = Clamp(d.LabelOffsetX, -300f, 300f,   0f);
                LabelOffsetY     = Clamp(d.LabelOffsetY, -300f, 300f, -26f);
                LabelScale       = Clamp(d.LabelScale,    0.2f,   3f,   1f);

                BannerTopColor    = Rgb(d.BannerTopR,    d.BannerTopG,    d.BannerTopB);
                BannerBottomColor = Rgb(d.BannerBottomR, d.BannerBottomG, d.BannerBottomB);
            }
            else ResetRays();

            // NOT guarded by Pos: 0 is a meaningful value here ("no cap"), and Pos rejects
            // anything non-positive. Clamped rather than defaulted, so a hand-typed 3.5 survives.
            //
            // The migration is deliberately as NARROW as it can be: only a pre-version-2 file whose
            // value is exactly 0 is discarded. That is the one value the previous version could
            // write without the user ever having chosen it — saving any other slider wrote the
            // then-default 0 along with it. A pre-version-2 file holding 4.5 means somebody moved
            // that slider on purpose, and a migration has no business overruling them.
            bool unchosenZero = d.Version < 2 && d.DigHoldSeconds == 0f;

            DigHoldSeconds = !unchosenZero && float.IsFinite(d.DigHoldSeconds) && d.DigHoldSeconds >= 0f
                                 ? MathF.Min(d.DigHoldSeconds, 60f)
                                 : PropService.DefaultHoldMilliseconds / 1000f;

            // Version 3 site defaults. Same narrow rule as above: overrule the stored number ONLY
            // when it is exactly the old default, i.e. the value the previous version wrote for
            // anyone who never touched that slider. A site deliberately set to 90 stays at 90.
            if (d.Version < 3)
            {
                if (SiteSize       == 70f) SiteSize       = 50f;
                if (SiteWallHeight == 6f)  SiteWallHeight = 1.5f;
            }

            // Version 4: the dig default moved from the Long Dig to the Short Dig. Same narrow
            // rule — 9 was the previous default, so a pre-v4 file holding exactly 9 is somebody who
            // never moved that slider, not somebody who wants a long dig.
            if (d.Version < 4 && DigHoldSeconds == PropService.LongDigMilliseconds / 1000f)
                DigHoldSeconds = PropService.ShortDigMilliseconds / 1000f;

            DigSpeed = float.IsFinite(d.DigSpeed) && d.DigSpeed > 0f
                           ? Math.Clamp(d.DigSpeed, PropService.MinSpeed, PropService.MaxSpeed)
                           : 1f;

            DigSpeedMethod = d.DigSpeedMethod is { } m
                                 ? Math.Clamp(m, 0, 3)
                                 : (int)PropService.SpeedMode.SlotFunction;

            // Version 5: Everything was the default only while it was unknown which lever worked.
            // Both per-slot levers were then measured to work alone, so a stored Everything is
            // almost certainly the old default rather than a choice — and it needlessly writes the
            // character-wide OverallSpeed. Same narrow rule: only the exact old default is
            // overruled.
            if (d.Version < 5 && DigSpeedMethod == (int)PropService.SpeedMode.Everything)
                DigSpeedMethod = (int)PropService.SpeedMode.SlotFunction;

            // Version 6: the HUD drop shipped for one version at 14, which was too low once the
            // clue gap was doing the real work. Same narrow rule as every migration above — only
            // the exact old default is overruled, because that is the one value the previous
            // version wrote for anyone who never touched the slider. A deliberately chosen 14 is
            // indistinguishable from an untouched one and is the price of that rule; every other
            // number survives.
            if (d.Version < 6 && HudDropPx == 14f) HudDropPx = DefaultHudDropPx;

            Apply();
            Diag.Info("[Dig] tuning loaded.");
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] tuning load failed, using defaults: {ex.Message}");
            ResetAll();
        }
    }

    private static float Pos(float v, float fallback) =>
        float.IsFinite(v) && v > 0f ? v : fallback;

    private static float Clamp(float v, float lo, float hi, float fallback) =>
        float.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;

    private static Vector3 Rgb(float r, float g, float b) =>
        new(Math.Clamp(float.IsFinite(r) ? r : 0f, 0f, 1f),
            Math.Clamp(float.IsFinite(g) ? g : 0f, 0f, 1f),
            Math.Clamp(float.IsFinite(b) ? b : 0f, 0f, 1f));

    public static void Save()
    {
        try
        {
            var dto = new Dto
            {
                Version = CurrentVersion,
                HuntNearby = HuntNearby, HuntWarm = HuntWarm, HuntHot = HuntHot, HuntDig = HuntDig,
                HuntMinPlacement = HuntMinPlacement, HuntMaxPlacement = HuntMaxPlacement,
                SiteSize = SiteSize, SitePieces = SitePieces,
                SitePieceRadius = SitePieceRadius, SitePieceSpacing = SitePieceSpacing,
                SiteWallHeight = SiteWallHeight, SiteGridCells = SiteGridCells,
                SitePieceMaxDrop = SitePieceMaxDrop, SitePieceMaxStep = SitePieceMaxStep,
                SiteColorR = SiteColor.X, SiteColorG = SiteColor.Y, SiteColorB = SiteColor.Z,
                SiteRevealPieces = SiteRevealPieces, SiteShowGrid = SiteShowGrid,
                SiteDebugColorR = SiteDebugColor.X,
                SiteDebugColorG = SiteDebugColor.Y,
                SiteDebugColorB = SiteDebugColor.Z,
                TrailDig = TrailDig, TrailRadar = TrailRadar,
                RadarFadeSeconds = RadarFadeSeconds,
                MaxRise = MaxRise, DigHoldSeconds = DigHoldSeconds, DigSpeed = DigSpeed,
                DigSpeedMethod = DigSpeedMethod,
                HudDropPx = HudDropPx, HudClueGapPx = HudClueGapPx,
                ClueStyleSaved = true,
                ClueFaceR = ClueFaceColor.X, ClueFaceG = ClueFaceColor.Y, ClueFaceB = ClueFaceColor.Z,
                ClueOutlineR = ClueOutlineColor.X,
                ClueOutlineG = ClueOutlineColor.Y,
                ClueOutlineB = ClueOutlineColor.Z,
                ClueOutline = ClueOutline, ClueShadow = ClueShadow, ClueGradient = ClueGradient,
                ClueOutlineWidth = ClueOutlineWidth, ClueShadowOffset = ClueShadowOffset,
                ClueFontSize = ClueFontSize,
                RaysSaved = true,
                RaysEnabled = RaysEnabled, RayCount = RayCount, RayReach = RayReach,
                RayOpacity = RayOpacity, RaySpeed = RaySpeed,
                RayColorR = RayColor.X, RayColorG = RayColor.Y, RayColorB = RayColor.Z,
                RayWidthVariance = RayWidthVariance, RaySpeedVariance = RaySpeedVariance,
                RayReachVariance = RayReachVariance, RayMinLength = RayMinLength,
                RayFalloff = RayFalloff, RayEdgeFade = RayEdgeFade,
                IconGradientDrop = IconGradientDrop,
                LabelOffsetX = LabelOffsetX, LabelOffsetY = LabelOffsetY, LabelScale = LabelScale,
                BannerTopR = BannerTopColor.X, BannerTopG = BannerTopColor.Y,
                BannerTopB = BannerTopColor.Z,
                BannerBottomR = BannerBottomColor.X, BannerBottomG = BannerBottomColor.Y,
                BannerBottomB = BannerBottomColor.Z,
            };

            // Temp-then-replace, like the completion stores: a half-written tuning file read on the
            // next launch would silently reset every range to its fallback.
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            File.Copy(tmp, Path, overwrite: true);
            File.Delete(tmp);
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] tuning save failed: {ex.Message}");
        }
    }
}
#endif
