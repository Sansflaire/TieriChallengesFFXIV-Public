#if DEV_BUILD
using System;
using System.IO;
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

    /// <summary>Side length of the drawn square site, in yalms.</summary>
    public static float SiteSize = 70f;

    /// <summary>How many pieces are buried inside it. Collect them all to get the relic.</summary>
    public static int SitePieces = 5;

    /// <summary>How close a dig must be to a piece to turn it up.</summary>
    public static float SitePieceRadius = 4f;

    /// <summary>Minimum gap between two buried pieces, so no dig can turn up two at once.</summary>
    public static float SitePieceSpacing = 12f;

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

    // ── persistence ──────────────────────────────────────────────────────────

    private sealed class Dto
    {
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
        public int   TrailStops { get; set; }
        public float TrailDig { get; set; }
        public float TrailRadar { get; set; }
        public float TrailSpacing { get; set; }
        public float MaxRise { get; set; }
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
        SiteSize = 70f; SitePieces = 5; SitePieceRadius = 4f; SitePieceSpacing = 12f;
    }

    public static void ResetTrail()
    {
        TrailStops = 4; TrailDig = 4f; TrailRadar = 30f; TrailSpacing = 90f;
    }

    public static void ResetAll()
    {
        ResetHunt(); ResetSite(); ResetTrail();
        MaxRise = 25f;
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
            TrailStops       = d.TrailStops  > 0 ? Math.Min(d.TrailStops, 20) : 4;
            TrailDig         = Pos(d.TrailDig,          4f);
            TrailRadar       = Pos(d.TrailRadar,       30f);
            TrailSpacing     = Pos(d.TrailSpacing,     90f);
            MaxRise          = Pos(d.MaxRise,          25f);

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
                HuntNearby = HuntNearby, HuntWarm = HuntWarm, HuntHot = HuntHot, HuntDig = HuntDig,
                HuntMinPlacement = HuntMinPlacement, HuntMaxPlacement = HuntMaxPlacement,
                SiteSize = SiteSize, SitePieces = SitePieces,
                SitePieceRadius = SitePieceRadius, SitePieceSpacing = SitePieceSpacing,
                TrailStops = TrailStops, TrailDig = TrailDig,
                TrailRadar = TrailRadar, TrailSpacing = TrailSpacing,
                MaxRise = MaxRise,
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
