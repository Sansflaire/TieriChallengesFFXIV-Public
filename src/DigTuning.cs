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
/// <para><b>Why these are not consts.</b> Trist asked for a debug way to define the ranges, and the
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
    /// Side length of the drawn square site, in yalms. 50, not 70 — Trist asked for roughly half
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

    /// <summary>How many spots the trail runs through before the payoff.</summary>
    public static int TrailStops = 4;

    /// <summary>How close a dig must be to a trail spot to turn up the next clue.</summary>
    public static float TrailDig = 4f;

    /// <summary>
    /// The radar appears at this distance and pulses faster the closer you get. It must always be
    /// larger than <see cref="TrailDig"/> — a radar that only shows up once you are already in
    /// range tells you nothing you did not already know.
    /// </summary>
    public static float TrailRadar = 30f;

    /// <summary>Roughly how far apart consecutive spots on the trail are placed.</summary>
    public static float TrailSpacing = 90f;

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
    private const int CurrentVersion = 5;

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
        public int   TrailStops { get; set; }
        public float TrailDig { get; set; }
        public float TrailRadar { get; set; }
        public float TrailSpacing { get; set; }
        public float MaxRise { get; set; }
        public float DigHoldSeconds { get; set; }
        public float DigSpeed { get; set; }
        public int?  DigSpeedMethod { get; set; }
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
        SitePieceMaxDrop = 2f;
        SiteRevealPieces = true; SiteDebugColor = DefaultDebugColor; SiteShowGrid = true;
    }

    public static void ResetTrail()
    {
        TrailStops = 4; TrailDig = 4f; TrailRadar = 30f; TrailSpacing = 90f;
    }

    public static void ResetAll()
    {
        ResetHunt(); ResetSite(); ResetTrail();
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
            TrailStops       = d.TrailStops  > 0 ? Math.Min(d.TrailStops, 20) : 4;
            TrailDig         = Pos(d.TrailDig,          4f);
            TrailRadar       = Pos(d.TrailRadar,       30f);
            TrailSpacing     = Pos(d.TrailSpacing,     90f);
            MaxRise          = Pos(d.MaxRise,          25f);

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
                SitePieceMaxDrop = SitePieceMaxDrop,
                SiteColorR = SiteColor.X, SiteColorG = SiteColor.Y, SiteColorB = SiteColor.Z,
                SiteRevealPieces = SiteRevealPieces, SiteShowGrid = SiteShowGrid,
                SiteDebugColorR = SiteDebugColor.X,
                SiteDebugColorG = SiteDebugColor.Y,
                SiteDebugColorB = SiteDebugColor.Z,
                TrailStops = TrailStops, TrailDig = TrailDig,
                TrailRadar = TrailRadar, TrailSpacing = TrailSpacing,
                MaxRise = MaxRise, DigHoldSeconds = DigHoldSeconds, DigSpeed = DigSpeed,
                DigSpeedMethod = DigSpeedMethod,
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
