#if DEV_BUILD
using System;
using System.Collections.Generic;
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


    // ── Test 4: Wild Trail ───────────────────────────────────────────────────

    /// <summary>How many spots are buried, and how far apart they must be.</summary>
    public static int   RoamStops   = 5;
    public static float RoamSpacing = 25f;

    /// <summary>
    /// Closest and furthest a spot may be buried from where the trail was started.
    ///
    /// <para>Not from the PREVIOUS stop — every spot is placed relative to the player at the moment
    /// of starting, so the whole trail fits in one walkable neighbourhood. Chaining each from the
    /// last would let a run wander off the map's edge one hop at a time.</para>
    /// </summary>
    /// <summary>
    /// Nearest a spot may be to the player, and an OPTIONAL cap on how far.
    ///
    /// <para><b>The cap defaults to 0, meaning none — the whole map is eligible.</b> Spots are drawn
    /// from the map's own world rectangle, so anywhere the drawn map covers can hold one. An earlier
    /// version sampled a ring around the player, which let "how far did you walk before pressing
    /// Start" decide what the trail was allowed to contain.</para>
    /// </summary>
    public static float RoamMinRange = 15f;
    public static float RoamMaxRange = 0f;

    /// <summary>
    /// How much open space under a candidate makes it a roof rather than ground.
    ///
    /// <para>This replaces the player-relative height gate, which could not survive whole-map
    /// sampling: across a zone it rejects every hill and basement merely for being far from where
    /// the player stands. Asking "is there more ground a long way below this" is local, so it stays
    /// true anywhere on the map.</para>
    /// </summary>
    public static float RoamMaxRise = 3.5f;

    /// <summary>
    /// How far a candidate may be from the navmesh and still count as standable.
    ///
    /// <para>Not zero, because the navmesh is a simplified surface: it sits a little above or below
    /// the visual floor and its edges are pulled in from walls by the character's radius. A spot on
    /// real pavement can easily be a yalm off it. Too large and the tolerance lets the far side of a
    /// wall back in, which is the whole thing being prevented.</para>
    /// </summary>
    public static float RoamNavTolerance = 2f;

    // RoamRequireNavmesh was REMOVED at 0.84.44.1 and must not come back as a setting.
    //
    // Its off position fell back to raycast heuristics, and those cannot answer reachability even in
    // principle: a ray finds the first solid surface under a position, and the sand outside a housing
    // ward's walls is solid, meshes, and is unreachable. So "off" did not trade accuracy for
    // availability — it produced spots nobody could walk to, while looking like a choice somebody
    // might reasonably make. vnavmesh is now a hard requirement of Test 4; see
    // DigRoamService.RefuseWithoutNavmesh.
    //
    // Old dig-tuning.json files still carry the key. It is simply not read, which is the correct
    // outcome: Newtonsoft ignores a property the DTO no longer has.

    /// <summary>How close a dig must be to a wild spot, and where its radar appears.</summary>
    public static float RoamDig   = 4f;
    public static float RoamRadar = 30f;

    /// <summary>
    /// How much a clue withholds. 0 names a landmark, a direction and a distance; 1 gives two
    /// landmarks and no bearing, leaving the player to intersect them.
    /// </summary>
    public static float RoamDifficulty = 0.35f;

    /// <summary>
    /// Which KINDS of clue the generator may write, as a bitmask of <c>ClueCategory</c>.
    ///
    /// <para>A bitmask rather than seven bools because it is seven bools that are always read
    /// together, always saved together, and always drawn as one row of checkboxes — and because
    /// adding an eighth category should not mean adding an eighth field to the save file, the
    /// migration and the reset. Append bits, never renumber: a saved mask stores raw bit positions.</para>
    ///
    /// <para>Turning a category off is the diagnostic for "this category writes bad clues", and it is
    /// the reason this is a setting rather than a constant. 127 is every bit through Enemy.</para>
    /// </summary>
    public static int RoamClueCategories = 127;

    /// <summary>
    /// How hard a just-used clue category is knocked down in the next draw. 0 means no anti-repeat at
    /// all; 0.85 means it keeps 15% of its share and climbs back over the following few clues.
    ///
    /// <para><b>A weight and not a ban, deliberately.</b> On a map where only two categories can say
    /// anything, a ban deadlocks on the third clue; a weight just lets one come round again.</para>
    /// </summary>
    public static float RoamRepeatPenalty = 0.85f;

    /// <summary>
    /// How willing a clue is to anchor itself to something that is NOT the nearest landmark.
    ///
    /// <para>0 always picks the nearest, which is the old behaviour and is kept reachable on purpose:
    /// it is the one-move test that separates "these clues are cruel" from "these clues are wrong".
    /// 1 lets a clue reach right down the distance-ordered list, which is what produces a true but
    /// irritating "Far EAST of somewhere on the west side".</para>
    /// </summary>
    public static float RoamAwkwardness = 0.5f;

    /// <summary>
    /// Reject a candidate whose nearest map marker is further than this, in yalms. 0 disables it.
    ///
    /// <para><b>This is the containment that follows the zone's SHAPE, which a box cannot.</b> A
    /// housing ward is not a rectangle: a box drawn round Empyreum has corners well outside the
    /// walls, and spots kept landing in them. The markers, though — plot numbers, subdivision
    /// labels, shops — blanket the walkable area densely and stop dead at its edge. So "how far is
    /// the nearest marker" is a cheap proxy for "am I still inside the place", and it traces the
    /// outline for free.</para>
    ///
    /// <para><b>It is a proxy and not a proof.</b> A large open field legitimately has no marker
    /// near its middle, which is why this is tunable and why 0 turns it off — on a sparse map it
    /// would reject most of the zone. It earns its keep on dense maps, which is exactly where the
    /// box fails worst.</para>
    /// </summary>
    /// <summary>
    /// <b>Defaults to 0 — OFF — since null zones arrived.</b> At 40 it rejected 2628 of 10000
    /// candidates and its partner wall test another 2647, and the two together carved the open
    /// middle out of every plaza: both are anchored to map MARKERS, so placements cluster in a halo
    /// around markers and the spaces between them go empty. That is marker density showing through,
    /// not walkability.
    ///
    /// <para>They were proxies for "where can a player stand", built because nothing else could
    /// answer. A hand-drawn box plus hand-drawn null zones answers it directly, so the proxy's cost
    /// is no longer worth paying. Kept, because on a map nobody has hand-authored it is still better
    /// than nothing.</para>
    /// </summary>
    public static float RoamMaxAnchorDistance;

    /// <summary>
    /// The height change one stride may absorb when walking a line to test it. Above this the step
    /// is a wall rather than a slope.
    ///
    /// <para><b>A knob because it decides over- versus under-rejection, and that failure has already
    /// happened.</b> The first version of the walk test used a single straight ray at fixed height
    /// and reported 46 of 57 spots blocked including ones on open paving. Too small a value here
    /// reproduces that by treating every kerb as a wall; too large lets the test walk up the side of
    /// a building. The right number is a property of FFXIV's movement, which is findable by trying
    /// it and not by reasoning about it.</para>
    /// </summary>
    public static float WalkMaxStep = 1.1f;

    /// <summary>
    /// Names a clue anchor may never be, comma-separated, matched case-insensitively as substrings.
    ///
    /// <para><b>A clue anchor has to be a place somebody could find on a map.</b> A Striking Dummy is
    /// housing furniture: it is wherever a player put it, it is gone when they remove it, and
    /// "north-east of the Striking Dummy" is unfollowable by anyone who has not already seen it.
    /// The name is real game data and the object is genuinely there, which is exactly why no
    /// automatic rule catches it — nothing about the sheet distinguishes a landmark from a
    /// furnishing.</para>
    ///
    /// <para><b>Editable because this list will grow.</b> Housing wards are full of placeable props
    /// with legitimate names, and each one only reveals itself by turning up in a bad clue. A
    /// setting means the next one costs a keystroke instead of a release.</para>
    /// </summary>
    public static string AnchorBans = "Striking Dummy,Mannequin,Orchestrion,Retainer Bell,Summoning Bell";

    /// <summary>
    /// A HAND-SET walkable box in WORLD coordinates, and the territory it belongs to.
    ///
    /// <para><b>This outranks every derived box, and it exists because all three derivations were
    /// wrong in Empyreum.</b> The map rectangle is a mostly-empty square; the navmesh box swallowed
    /// terrain nobody can stand on; the landmark spread is the best of them and still only covers
    /// where the game puts labels. Walking to two opposite corners and pressing a button involves no
    /// conversion, no sheet, no inference — it is the one measurement that cannot be wrong about the
    /// thing it measures.</para>
    ///
    /// <para><b>Keyed to a territory on purpose.</b> A box set in Empyreum must never silently apply
    /// in a field zone; when the stored territory does not match, the box is ignored and the derived
    /// chain takes over.</para>
    /// </summary>
    public static uint  BoxTerritory;
    public static bool  BoxSet;
    public static float BoxMinX, BoxMinZ, BoxMaxX, BoxMaxZ;

    /// <summary>
    /// A rectangle, in WORLD coordinates, that placement must never put a spot inside.
    ///
    /// <para><b>The complement of the walkable box, and the piece that makes hand-authoring
    /// workable.</b> A box says where the zone is; it cannot say that the lake in the middle of it,
    /// or the roof of the building on its east side, are not places to bury anything. Every
    /// automatic attempt at that distinction has failed — the navmesh filter, marker proximity, the
    /// wall tests — and each failure cost a version. Drawing the exclusions by hand takes a minute
    /// and cannot be wrong about what it was told.</para>
    /// </summary>
    public sealed class NullZone
    {
        public uint  Territory { get; set; }
        public float MinX { get; set; }
        public float MinZ { get; set; }
        public float MaxX { get; set; }
        public float MaxZ { get; set; }
    }

    /// <summary>Hand-drawn exclusions, across all territories. Filtered by territory on use.</summary>
    public static List<NullZone> NullZones = new();

    /// <summary>Whether a world position falls inside any exclusion for this territory.</summary>
    public static bool InNullZone(uint territory, float x, float z)
    {
        foreach (var n in NullZones)
        {
            if (n.Territory != territory) continue;
            if (x < n.MinX || x > n.MaxX || z < n.MinZ || z > n.MaxZ) continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Collapses the exclusions for one territory into as few rectangles as exactly possible.
    ///
    /// <para><b>Two merges, both exact, and deliberately not a polygon union.</b> An L-shaped union
    /// of two rectangles is not a rectangle, so a general union needs a different shape entirely —
    /// and the check it feeds would stop being four float comparisons. These two cases cover what
    /// hand-drawing actually produces: dragging over the same area twice, and dragging a strip in
    /// several passes.</para>
    /// <list type="number">
    /// <item><b>Containment.</b> A rectangle wholly inside another is dropped. Exact: the union is
    /// unchanged.</item>
    /// <item><b>Colinear merge.</b> Two rectangles whose spans match on one axis, and which touch or
    /// overlap on the other, become one. Exact when the matching axis matches perfectly.</item>
    /// </list>
    ///
    /// <para><b>The tolerance only ever GROWS a zone.</b> Hand-drawn rectangles never align to the
    /// yalm, so near-matching edges are merged to their OUTER bound. That makes the merged zone a
    /// superset of the two originals — it can exclude slightly more than was drawn, never less, so
    /// it can never let through a spot the drawn zones would have caught. Growing an exclusion is
    /// the safe direction to be approximate in; shrinking one is not, which is why the union is
    /// taken rather than the intersection.</para>
    /// </summary>
    public static int MergeNullZones(uint territory, float tolerance = 2f)
    {
        int before = 0;
        foreach (var n in NullZones) if (n.Territory == territory) before++;

        bool changed = true;

        // Repeated to fixpoint: merging two rectangles can make the result contain a third, and one
        // pass would leave that one behind looking like the merge had failed.
        while (changed)
        {
            changed = false;

            for (int i = 0; i < NullZones.Count && !changed; i++)
            {
                if (NullZones[i].Territory != territory) continue;

                for (int j = i + 1; j < NullZones.Count && !changed; j++)
                {
                    if (NullZones[j].Territory != territory) continue;

                    var a = NullZones[i];
                    var b = NullZones[j];

                    if (Contains(a, b)) { NullZones.RemoveAt(j); changed = true; break; }
                    if (Contains(b, a)) { NullZones.RemoveAt(i); changed = true; break; }

                    if (!TryColinear(a, b, tolerance, out var merged)) continue;

                    NullZones[i] = merged;
                    NullZones.RemoveAt(j);
                    changed = true;
                }
            }
        }

        Save();

        int after = 0;
        foreach (var n in NullZones) if (n.Territory == territory) after++;

        return before - after;
    }

    private static bool Contains(NullZone outer, NullZone inner) =>
        inner.MinX >= outer.MinX && inner.MaxX <= outer.MaxX &&
        inner.MinZ >= outer.MinZ && inner.MaxZ <= outer.MaxZ;

    /// <summary>Two rectangles that line up on one axis and meet on the other, as one rectangle.</summary>
    private static bool TryColinear(NullZone a, NullZone b, float tol, out NullZone merged)
    {
        merged = null!;

        bool sameX = MathF.Abs(a.MinX - b.MinX) <= tol && MathF.Abs(a.MaxX - b.MaxX) <= tol;
        bool sameZ = MathF.Abs(a.MinZ - b.MinZ) <= tol && MathF.Abs(a.MaxZ - b.MaxZ) <= tol;

        // Overlapping or touching on the other axis, within the same tolerance so a one-yalm gap
        // left by hand does not prevent the merge.
        bool meetZ = a.MinZ <= b.MaxZ + tol && b.MinZ <= a.MaxZ + tol;
        bool meetX = a.MinX <= b.MaxX + tol && b.MinX <= a.MaxX + tol;

        if (!((sameX && meetZ) || (sameZ && meetX))) return false;

        merged = new NullZone
        {
            Territory = a.Territory,
            MinX = MathF.Min(a.MinX, b.MinX), MaxX = MathF.Max(a.MaxX, b.MaxX),
            MinZ = MathF.Min(a.MinZ, b.MinZ), MaxZ = MathF.Max(a.MaxZ, b.MaxZ),
        };

        return true;
    }

    /// <summary>Adds an exclusion, normalising the corners so min really is min.</summary>
    public static void AddNullZone(uint territory, float x0, float z0, float x1, float z1)
    {
        // A stray click would otherwise store a zero-area rectangle that excludes nothing and
        // clutters the list forever.
        if (MathF.Abs(x1 - x0) < 0.5f || MathF.Abs(z1 - z0) < 0.5f) return;

        NullZones.Add(new NullZone
        {
            Territory = territory,
            MinX = MathF.Min(x0, x1), MaxX = MathF.Max(x0, x1),
            MinZ = MathF.Min(z0, z1), MaxZ = MathF.Max(z0, z1),
        });

        // Merged as it is drawn, so overlapping passes over the same area collapse immediately
        // rather than accumulating into a list nobody wants to look at.
        MergeNullZones(territory);
    }

    public static void RemoveNullZone(NullZone zone)
    {
        NullZones.Remove(zone);
        Save();
    }

    /// <summary>Drops every exclusion for one territory, leaving other zones' work alone.</summary>
    public static void ClearNullZones(uint territory)
    {
        NullZones.RemoveAll(n => n.Territory == territory);
        Save();
    }

    /// <summary>Records one corner from a world position, normalising so min really is min.</summary>
    public static void SetBoxCorner(bool first, uint territory, float x, float z)
    {
        // A corner set in a different zone than the other one would make a box spanning two maps.
        // Changing territory therefore restarts the box rather than extending it.
        if (!BoxSet || BoxTerritory != territory)
        {
            BoxTerritory = territory;
            BoxMinX = BoxMaxX = x;
            BoxMinZ = BoxMaxZ = z;
            BoxSet  = true;
        }

        if (first) { BoxMinX = x; BoxMinZ = z; }
        else       { BoxMaxX = x; BoxMaxZ = z; }

        if (BoxMinX > BoxMaxX) (BoxMinX, BoxMaxX) = (BoxMaxX, BoxMinX);
        if (BoxMinZ > BoxMaxZ) (BoxMinZ, BoxMaxZ) = (BoxMaxZ, BoxMinZ);

        Save();
    }

    /// <summary>
    /// Writes one corner during a DRAG, without normalising and without saving.
    ///
    /// <para><b>Both omissions are deliberate.</b> Normalising mid-drag swaps min and max the moment
    /// the pointer crosses the opposite corner, so the handle being held jumps to the other side of
    /// the box and the drag inverts under the cursor. Saving on every frame of a drag writes the
    /// JSON file sixty times a second. <see cref="FinishBoxDrag"/> does both, once, on release.</para>
    /// </summary>
    public static void DragBoxCorner(bool first, uint territory, float x, float z)
    {
        if (!BoxSet || BoxTerritory != territory)
        {
            BoxTerritory = territory;
            BoxMinX = BoxMaxX = x;
            BoxMinZ = BoxMaxZ = z;
            BoxSet  = true;
        }

        if (first) { BoxMinX = x; BoxMinZ = z; }
        else       { BoxMaxX = x; BoxMaxZ = z; }
    }

    /// <summary>Normalises and persists after a drag ends.</summary>
    public static void FinishBoxDrag()
    {
        if (BoxMinX > BoxMaxX) (BoxMinX, BoxMaxX) = (BoxMaxX, BoxMinX);
        if (BoxMinZ > BoxMaxZ) (BoxMinZ, BoxMaxZ) = (BoxMaxZ, BoxMinZ);
        Save();
    }

    /// <summary>Seeds the manual box from a derived one, so there is something to drag.</summary>
    public static void SeedBox(uint territory, float minX, float minZ, float maxX, float maxZ)
    {
        BoxTerritory = territory;
        BoxMinX = minX; BoxMinZ = minZ;
        BoxMaxX = maxX; BoxMaxZ = maxZ;
        BoxSet  = true;
        Save();
    }

    public static void ClearBox()
    {
        BoxSet = false;
        BoxTerritory = 0;
        BoxMinX = BoxMinZ = BoxMaxX = BoxMaxZ = 0f;
        Save();
    }

    /// <summary>The hand-set box for this territory, if one was set here and it is not degenerate.</summary>
    public static bool TryManualBox(uint territory, out float minX, out float minZ,
                                    out float maxX, out float maxZ)
    {
        minX = BoxMinX; minZ = BoxMinZ; maxX = BoxMaxX; maxZ = BoxMaxZ;

        return BoxSet
            && BoxTerritory == territory
            && maxX - minX > 5f
            && maxZ - minZ > 5f;
    }

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

    /// <summary>
    /// The dark outline around the dig artwork: whether it is drawn at all, how far it reaches past
    /// the silhouette, and how opaque it is.
    ///
    /// <para><b>It is already an OUTER outline</b> — the shape is stamped in dark behind, then the
    /// artwork is drawn on top at its true size and position, so the dark only survives where it
    /// sticks out past the art. Nothing is inset and the artwork is never shrunk.</para>
    ///
    /// <para><b>Two things can still make it read as eating into the image, and both are width.</b>
    /// The first is that the artwork's own edge is antialiased, so its outermost pixels are
    /// partially transparent and the dark behind shows THROUGH them — a fringe just inside the
    /// visible edge, which gets more obvious the more opaque the outline is. The second is that a
    /// stamp cannot tell the outside of a shape from a hole inside it, so any transparent detail
    /// WITHIN the artwork fills with dark too, and at a wide offset that dark creeps in from both
    /// sides of every interior line. Narrow is therefore the fix for both, which is why this
    /// defaults thinner than it used to and can be switched off outright.</para>
    /// </summary>
    public static bool  IconOutline      = true;
    public static float IconOutlineWidth = 1.0f;
    public static float IconOutlineAlpha = 0.85f;

    /// <summary>
    /// Motion echoes behind a sliding trail banner: how many, how far apart in time, and how
    /// quickly they fade back.
    ///
    /// <para><b>Step is in SECONDS, not pixels, and that is what makes the trail follow the
    /// easing.</b> Each echo is the banner drawn where it actually was that many seconds ago, so
    /// they bunch up as it decelerates into place and stretch out as it accelerates away — which is
    /// what a real motion blur does. Fixed pixel offsets would give an evenly spaced comb that
    /// looks the same whether the banner is flying or nearly stopped.</para>
    ///
    /// <para>Echoes appear only while the banner is actually moving; during the hold every echo
    /// would land on the same spot and simply darken it.</para>
    /// </summary>
    public static bool  BannerEcho        = true;
    public static int   BannerEchoCount   = 6;
    public static float BannerEchoStep    = 0.045f;
    public static float BannerEchoFalloff = 0.60f;

    /// <summary>
    /// Where a trail banner sits and how big it is drawn.
    ///
    /// <para>Y is a FRACTION of the viewport's height, and X a fraction of its width, deliberately —
    /// a banner is a screen-scale announcement, so it should land in the same place on a 1080p
    /// monitor as on a 1440p one. Pixels would put it a third of the way down one and a quarter of
    /// the way down the other.</para>
    ///
    /// <para>Width is also a fraction, so the banner keeps the same presence on screen at any
    /// resolution; the height follows from the artwork's 2:1 shape and is never set separately.</para>
    /// </summary>
    public static float BannerY     = 0.28f;
    public static float BannerX     = 0f;
    public static float BannerWidth = 0.46f;

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
        public bool? RoamSaved { get; set; }

        public int   RoamStops { get; set; }
        public float RoamSpacing { get; set; }
        public float RoamMinRange { get; set; }
        public float RoamMaxRange { get; set; }
        public float RoamMaxRise { get; set; }
        public float RoamNavTolerance { get; set; }
        // RoamRequireNavmesh removed at 0.84.44.1 — see the note beside the fields. A stale key in an
        // existing dig-tuning.json is ignored on load, which is the behaviour we want.
        public float RoamDig { get; set; }
        public float RoamRadar { get; set; }
        public float RoamDifficulty { get; set; }

        /// <summary>
        /// Nullable for the same reason as the HUD pair: 0 is a legitimate choice for every one of
        /// these — no categories, no anti-repeat, no awkwardness — so absent must be distinguishable
        /// from chosen-zero. A file written before these existed leaves them null and gets the
        /// defaults, rather than being read as "the user turned every clue category off".
        /// </summary>
        public int?   RoamClueCategories { get; set; }
        public float? RoamRepeatPenalty { get; set; }
        public float? RoamAwkwardness { get; set; }
        public float? RoamMaxAnchorDistance { get; set; }
        public float?  WalkMaxStep { get; set; }
        public string? AnchorBans { get; set; }

        /// <summary>The hand-set walkable box. Absent on any file written before it existed.</summary>
        public uint?  BoxTerritory { get; set; }
        public bool?  BoxSet { get; set; }
        public float? BoxMinX { get; set; }
        public float? BoxMinZ { get; set; }
        public float? BoxMaxX { get; set; }
        public float? BoxMaxZ { get; set; }

        /// <summary>Hand-drawn exclusions. Absent on any file written before they existed.</summary>
        public List<NullZone>? NullZones { get; set; }

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
        public bool  IconOutline { get; set; }
        public float IconOutlineWidth { get; set; }
        public float IconOutlineAlpha { get; set; }
        public bool  BannerEcho { get; set; }
        public int   BannerEchoCount { get; set; }
        public float BannerEchoStep { get; set; }
        public float BannerEchoFalloff { get; set; }
        public float BannerY { get; set; }
        public float BannerX { get; set; }
        public float BannerWidth { get; set; }
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

    public static void ResetRoam()
    {
        RoamStops = 5; RoamSpacing = 25f; RoamMinRange = 15f; RoamMaxRange = 0f;
        RoamMaxRise = 3.5f; RoamNavTolerance = 2f;
        RoamDig = 4f; RoamRadar = 30f; RoamDifficulty = 0.35f;
        RoamClueCategories = 127; RoamRepeatPenalty = 0.85f; RoamAwkwardness = 0.5f;
        RoamMaxAnchorDistance = 0f; WalkMaxStep = 1.1f;
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
        IconOutline = true; IconOutlineWidth = 1.0f; IconOutlineAlpha = 0.85f;
        BannerEcho = true; BannerEchoCount = 6;
        BannerEchoStep = 0.045f; BannerEchoFalloff = 0.60f;
        BannerY = 0.28f; BannerX = 0f; BannerWidth = 0.46f;
    }

    public static void ResetAll()
    {
        ResetHunt(); ResetSite(); ResetTrail(); ResetRoam();
        ResetHud(); ResetClueStyle(); ResetRays();
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

            if (d.RoamSaved == true)
            {
                RoamStops      = d.RoamStops > 0 ? Math.Clamp(d.RoamStops, 1, 20) : 5;
                RoamSpacing    = Pos(d.RoamSpacing,   25f);
                // NOT Pos-guarded: 0 is meaningful for both — "no minimum" and, for the cap,
                // "no cap, use the whole map", which is the default.
                RoamMinRange   = Clamp(d.RoamMinRange, 0f, 400f,  15f);
                RoamMaxRange   = Clamp(d.RoamMaxRange, 0f, 2000f,  0f);
                RoamMaxRise      = Clamp(d.RoamMaxRise,      0f, 40f, 3.5f);
                RoamNavTolerance   = Clamp(d.RoamNavTolerance, 0.1f, 20f, 2f);
                RoamDig        = Pos(d.RoamDig,        4f);
                RoamRadar      = Pos(d.RoamRadar,     30f);
                RoamDifficulty = Clamp(d.RoamDifficulty, 0f, 1f, 0.35f);

                // The three clue knobs post-date RoamSaved, so they are read INSIDE this branch but
                // each defaults on its own. A file saved between the two versions has RoamSaved true
                // and these absent — reading them as chosen-zero would silently turn every clue
                // category off and make the generator look broken.
                RoamClueCategories = d.RoamClueCategories is { } cc && cc != 0
                                         ? cc & 127 : 127;
                RoamRepeatPenalty  = d.RoamRepeatPenalty is { } rp && float.IsFinite(rp)
                                         ? Math.Clamp(rp, 0f, 0.99f) : 0.85f;
                RoamAwkwardness    = d.RoamAwkwardness is { } aw && float.IsFinite(aw)
                                         ? Math.Clamp(aw, 0f, 1f) : 0.5f;

                // NARROW MIGRATION, the same shape as the dig-hold one: a stored value of EXACTLY
                // the old default is overruled, anything else was chosen on purpose and is left
                // alone. 40 was the default when this shipped and it turned out to carve the open
                // middle out of every plaza — markers cluster, so a radius around them is a halo,
                // not a boundary. Anyone who deliberately set 40 loses that choice once; everyone
                // else stops fighting a default nobody picked.
                RoamMaxAnchorDistance = d.RoamMaxAnchorDistance is { } ma && float.IsFinite(ma)
                                            ? (MathF.Abs(ma - 40f) < 0.001f ? 0f : Math.Clamp(ma, 0f, 2000f))
                                            : 0f;
                WalkMaxStep = d.WalkMaxStep is { } ws && float.IsFinite(ws)
                                  ? Math.Clamp(ws, 0.2f, 4f) : 1.1f;

                // Absent means never saved, so take the shipped list. An EMPTY string is a real
                // choice — somebody clearing the field wants no bans — and must survive a reload.
                if (d.AnchorBans != null) AnchorBans = d.AnchorBans;

                // Read OUTSIDE any default-on-absent guard: a file with no box must leave BoxSet
                // false rather than inventing a zero-sized one at the world origin, which would
                // override every derived box with a degenerate rectangle in the middle of nowhere.
                BoxSet       = d.BoxSet == true;
                BoxTerritory = d.BoxTerritory ?? 0u;
                BoxMinX      = d.BoxMinX ?? 0f;
                BoxMinZ      = d.BoxMinZ ?? 0f;
                BoxMaxX      = d.BoxMaxX ?? 0f;
                BoxMaxZ      = d.BoxMaxZ ?? 0f;

                // Hand-drawn work: never defaulted, never invented. An absent list means none were
                // drawn, which is not the same as drawing none and is treated identically anyway.
                NullZones = d.NullZones ?? new List<NullZone>();
            }
            else ResetRoam();

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

                IconOutline      = d.IconOutline;
                IconOutlineWidth = Clamp(d.IconOutlineWidth, 0f, 6f, 1.0f);
                IconOutlineAlpha = Clamp(d.IconOutlineAlpha, 0f, 1f, 0.85f);

                BannerEcho        = d.BannerEcho;
                BannerEchoCount   = d.BannerEchoCount > 0 ? Math.Clamp(d.BannerEchoCount, 1, 16) : 6;
                BannerEchoStep    = Clamp(d.BannerEchoStep,    0.005f, 0.30f, 0.045f);
                BannerEchoFalloff = Clamp(d.BannerEchoFalloff, 0.05f,  0.95f, 0.60f);

                BannerY     = Clamp(d.BannerY,     0f,   0.90f, 0.28f);
                BannerX     = Clamp(d.BannerX,    -0.5f, 0.5f,  0f);
                BannerWidth = Clamp(d.BannerWidth, 0.10f, 1.00f, 0.46f);
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
                RoamSaved = true,
                RoamStops = RoamStops, RoamSpacing = RoamSpacing,
                RoamMinRange = RoamMinRange, RoamMaxRange = RoamMaxRange,
                RoamMaxRise = RoamMaxRise, RoamNavTolerance = RoamNavTolerance,
                RoamDig = RoamDig, RoamRadar = RoamRadar, RoamDifficulty = RoamDifficulty,
                RoamClueCategories = RoamClueCategories,
                RoamRepeatPenalty = RoamRepeatPenalty, RoamAwkwardness = RoamAwkwardness,
                RoamMaxAnchorDistance = RoamMaxAnchorDistance, WalkMaxStep = WalkMaxStep,
                AnchorBans = AnchorBans,
                BoxSet = BoxSet, BoxTerritory = BoxTerritory,
                BoxMinX = BoxMinX, BoxMinZ = BoxMinZ, BoxMaxX = BoxMaxX, BoxMaxZ = BoxMaxZ,
                NullZones = NullZones,
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
                IconOutline = IconOutline, IconOutlineWidth = IconOutlineWidth,
                IconOutlineAlpha = IconOutlineAlpha,
                BannerEcho = BannerEcho, BannerEchoCount = BannerEchoCount,
                BannerEchoStep = BannerEchoStep, BannerEchoFalloff = BannerEchoFalloff,
                BannerY = BannerY, BannerX = BannerX, BannerWidth = BannerWidth,
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
