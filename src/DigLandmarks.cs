#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;

using LSheets = Lumina.Excel.Sheets;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. The named places the game itself draws on the current map, converted into
/// world positions so a clue can be written about them.
///
/// <para><b>This is game data, and the map screen is the proof.</b> Every label on an open map —
/// "Empyreum Subdivision", "Blue Badger Gate", an aetheryte plaza — is a <c>MapMarker</c> subrow
/// reached through <c>Map.MapMarkerRange</c>. There are 10,747 of them across 339 zones, 6,435 with
/// names. Nothing has to be scraped, guessed or hand-listed: if the player can see it on their map,
/// it is in this sheet with exact coordinates.</para>
///
/// <para><b>What it does NOT give, and this matters for clue writing.</b> The unnamed ~40% are the
/// icon-only markers — retainer bells, market boards, menders, individual house plots. They carry an
/// icon id rather than a name, and turning an icon id into the words "a retainer bell" needs a
/// lookup table this plugin does not have and must not invent. So clues are written from NAMED
/// landmarks only, which is a smaller set but one where every word is the game's own. See the lab's
/// landmark dump for what a given map actually offers before assuming a zone is well served.</para>
/// </summary>
internal static class DigLandmarks
{
    internal readonly record struct Landmark(string Name, Vector3 World, float MapX, float MapY);

    /// <summary>Landmarks for the map last asked about, and which map that was.</summary>
    private static uint _cachedMap;
    private static List<Landmark> _cached = new();

    /// <summary>Map extent in map coordinates, for the quadrant clues.</summary>
    private static float _mapSpan = 41f;

    /// <summary>
    /// Landmarks on the map the player is currently looking at, cached per map.
    ///
    /// <para><c>AgentMap.CurrentMapId</c> rather than the territory's sheet entry, for the reason
    /// recorded in <c>BROKEN.md</c> 006: a housing territory has a ward map AND a subdivision map
    /// with different offsets, and the sheet names only the ward. Empyreum Subdivision is exactly
    /// that case, so getting this wrong would put every landmark hundreds of yalms out in the one
    /// zone most likely to be tested in.</para>
    /// </summary>
    public static IReadOnlyList<Landmark> ForCurrentMap()
    {
        uint mapId = CurrentMapId();
        if (mapId == 0) return Array.Empty<Landmark>();
        if (mapId == _cachedMap) return _cached;

        _cachedMap = mapId;
        _cached    = Load(mapId, out _mapSpan);

        return _cached;
    }

    /// <summary>The map's width and height in map coordinates — the 1..42-ish the readout shows.</summary>
    public static float MapSpan { get { ForCurrentMap(); return _mapSpan; } }

    public static uint CurrentMapId()
    {
        try
        {
            unsafe
            {
                var agent = AgentMap.Instance();
                return agent == null ? 0u : agent->CurrentMapId;
            }
        }
        catch { return 0u; }
    }

    /// <summary>
    /// Reads one map's named markers and converts each to a world position.
    ///
    /// <para><b>The world conversion is the algebraic INVERSE of the two formulas this repo already
    /// verified</b>, not a third formula invented here. Both of these describe the same map point:
    /// </para>
    /// <code>
    ///   MarkerToMap(raw, sf) = 41/c * (raw/2048) + 1                        // marker space
    ///   WorldToMap (w,  sf, off) = 41/c * (((w+off)*c + 1024)/2048) + 1     // world space
    /// </code>
    /// <para>Setting them equal cancels the outer terms and leaves <c>raw = (w+off)*c + 1024</c>,
    /// hence <c>w = (raw - 1024)/c - off</c>. Nothing is assumed about the map's layout; if the two
    /// source formulas are right, this is right.</para>
    ///
    /// <para><b>Those formulas are spot-checked, not fully validated</b> — see the Game Data
    /// Cookbook §6D. That is why the lab has a dump button: standing at a landmark and reading zero
    /// yalms is the one-action proof, and until somebody does that this is derived rather than
    /// confirmed.</para>
    /// </summary>
    private static List<Landmark> Load(uint mapId, out float span)
    {
        var list = new List<Landmark>();
        span = 41f;

        try
        {
            var maps = Plugin.DataManager.GetExcelSheet<LSheets.Map>();
            if (maps?.GetRowOrDefault(mapId) is not { } map || map.MapMarkerRange == 0) return list;

            float c = MathF.Max(0.01f, map.SizeFactor / 100f);
            span = 41f / c;

            var markers = Plugin.DataManager.GetSubrowExcelSheet<LSheets.MapMarker>();
            if (markers?.GetRowOrDefault(map.MapMarkerRange) is not { } set) return list;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in set)
            {
                string name = m.PlaceNameSubtext.ValueNullable?.Name.ExtractText() ?? string.Empty;
                if (name.Length == 0) continue;

                // A two-line label is stored with an embedded newline — "Carline Canopy\n
                // (Adventurers' Guild)". Collapsed, because a clue is spoken as one phrase.
                name = name.Replace("\r", " ").Replace("\n", " ").Trim();
                while (name.Contains("  ")) name = name.Replace("  ", " ");

                if (name.Length == 0 || !seen.Add(name)) continue;

                float wx = (m.X - 1024f) / c - map.OffsetX;
                float wz = (m.Y - 1024f) / c - map.OffsetY;

                list.Add(new Landmark(
                    name,
                    new Vector3(wx, 0f, wz),
                    41f / c * (m.X / 2048f) + 1f,
                    41f / c * (m.Y / 2048f) + 1f));
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] landmark read failed: {ex.Message}");
        }

        return list;
    }

    /// <summary>The named landmark nearest a world position, or null when the map has none.</summary>
    public static Landmark? Nearest(Vector3 world, Landmark? exclude = null)
    {
        Landmark? best = null;
        float bestD = float.MaxValue;

        foreach (var l in ForCurrentMap())
        {
            if (exclude is { } e && string.Equals(e.Name, l.Name, StringComparison.Ordinal)) continue;

            float d = DigGround.Flat(world, l.World);
            if (d >= bestD) continue;

            bestD = d;
            best  = l;
        }

        return best;
    }

    /// <summary>
    /// Which part of the map a world position falls in — "NORTH-EAST", "the middle", and so on.
    ///
    /// <para><b>Worked in MAP coordinates, not world ones.</b> The player reads a quadrant off the
    /// map screen, and the map is not necessarily square with the world nor centred on the origin.
    /// Converting first means the answer is the one they can check.</para>
    ///
    /// <para>A central band is deliberately its own answer rather than being forced into a corner:
    /// a spot two yalms from the middle is not "north-east" in any useful sense, and saying so would
    /// send the player to the wrong quarter with full confidence.</para>
    /// </summary>
    public static string Quadrant(Vector3 world)
    {
        if (!WalkableBox(out var lo, out var hi)) return "somewhere on this map";

        float cx = (lo.X + hi.X) * 0.5f;
        float cz = (lo.Y + hi.Y) * 0.5f;

        // Inside this fraction from the centre counts as neither side. Measured per axis, because a
        // zone's walkable area is rarely square.
        const float Middle = 0.14f;
        float bandX = MathF.Max(1f, (hi.X - lo.X) * Middle);
        float bandZ = MathF.Max(1f, (hi.Y - lo.Y) * Middle);

        // WORLD axes: +X is east, +Z is south. Same convention as DigGround.Compass, and the reason
        // this no longer converts to map coordinates at all — see WalkableBox.
        string ns = world.Z < cz - bandZ ? "NORTH" : world.Z > cz + bandZ ? "SOUTH" : string.Empty;
        string ew = world.X > cx + bandX ? "EAST"  : world.X < cx - bandX ? "WEST"  : string.Empty;

        // "the middle BAND", not "the middle". This phrase is a 28%-wide strip on both axes — a
        // large region — and calling it "the middle of the map" reads as a point. Same defect as
        // the grid cells named "very centre": the words promised precision the clue cannot have.
        if (ns.Length == 0 && ew.Length == 0) return "the middle band of the map";
        if (ns.Length == 0) return $"the {ew} side of the map";
        if (ew.Length == 0) return $"the {ns} of the map";

        return $"the {ns}-{ew} of the map";
    }

    /// <summary>
    /// The extent of the map's actual CONTENT in map coordinates, not the extent of its coordinate
    /// system.
    ///
    /// <para><b>These are different and treating them as the same put "dead centre" on a spot that
    /// was visibly north-east.</b> Map coordinates run 1..1+41/c, which corresponds to raw 0..2048 —
    /// the whole square the map image could theoretically cover. A real map's geometry occupies only
    /// part of that square, and a housing subdivision occupies a small, off-centre part of it. So the
    /// midpoint of the coordinate range is the middle of a mostly-empty rectangle, not the middle of
    /// anywhere a player would recognise.</para>
    ///
    /// <para>Derived instead from the landmarks the game itself draws on the map, which by
    /// construction sit on the content. Falls back to the coordinate range when a map has too few
    /// named markers to bound anything — better a known-crude answer than one computed from three
    /// points that happen to be in one corner.</para>
    /// </summary>
    /// <summary>
    /// The walkable extent of this zone in <b>WORLD</b> coordinates as (x, z) pairs — the square
    /// every direction word is measured against.
    ///
    /// <para><b>World, not map, and that is the whole point.</b> A quadrant was reported as "very
    /// centre" for a spot sitting three-quarters of the way east, and the arithmetic was correct for
    /// the numbers it had: the spot converted to map x 14.1 against a box centre of 13.2. The flag
    /// the GAME placed from the same world position landed far further east, so the two conversions
    /// disagreed — and ours is the one this repo has always marked spot-checked-never-proven.</para>
    ///
    /// <para>So the conversion is gone from this path. The navmesh box is already world-space, the
    /// spot is world-space, and <c>+X is east, +Z is south</c> is the same convention
    /// <see cref="DigGround.Compass"/> uses and states its evidence for. Nothing has to be
    /// transformed to answer "is this east of the middle", so nothing is — which removes an entire
    /// class of error rather than correcting one instance of it.</para>
    ///
    /// <para>Falls back to the landmark spread, then the map rectangle, when the navmesh cannot
    /// bound the zone.</para>
    /// </summary>
    public static bool WalkableBox(out Vector2 lo, out Vector2 hi)
    {
        lo = hi = default;

        // ZEROTH: a box set by hand, for this territory.
        //
        // It outranks everything below because it is the only one that is a MEASUREMENT. The three
        // derived boxes are all inferences about where a player can be, and in Empyreum all three
        // were wrong in different ways. Walking to two corners involves no conversion, no sheet and
        // no assumption about what a navmesh flag means.
        if (DigTuning.TryManualBox(Plugin.ClientState.TerritoryType,
                                   out float bx0, out float bz0, out float bx1, out float bz1))
        {
            lo = new Vector2(bx0, bz0);
            hi = new Vector2(bx1, bz1);
            return true;
        }

        // FIRST: the spread of the map's own labels.
        //
        // THIS WAS DEMOTED IN FAVOUR OF THE NAVMESH AND THAT WAS A MISTAKE, reverted at 0.84.45.5
        // on direct evidence: the debug view draws both, and the labels hug the ward while the
        // navmesh box swallowed huge regions of terrain nobody can stand on. The reasoning for
        // preferring the navmesh — "it is the walkable area by definition" — rested on vnavmesh's
        // reachable filter meaning "the player can walk here". That was never verified; it is a
        // mesh-graph property whose flood-fill seed is unknown to us, and spots were landing far
        // outside the ward while passing it.
        //
        // The labels are a weaker signal in principle and the better one in fact. On a housing map
        // they are plot numbers spread across the whole ward; on a field map they are camps and
        // landmarks spread across the playable area. Neither covers open ground perfectly, and both
        // are unambiguously INSIDE the place a player can be — which the navmesh box was not.
        var marks = ForCurrentMap();

        if (marks.Count >= 4)
        {
            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;

            foreach (var m in marks)
            {
                if (m.World.X < minX) minX = m.World.X;
                if (m.World.X > maxX) maxX = m.World.X;
                if (m.World.Z < minZ) minZ = m.World.Z;
                if (m.World.Z > maxZ) maxZ = m.World.Z;
            }

            if (maxX - minX > 5f && maxZ - minZ > 5f)
            {
                float padX = (maxX - minX) * 0.10f;
                float padZ = (maxZ - minZ) * 0.10f;

                lo = new Vector2(minX - padX, minZ - padZ);
                hi = new Vector2(maxX + padX, maxZ + padZ);
                return true;
            }
        }

        // SECOND: the navmesh, for a map with too few labels to bound anything. Still better than
        // the raw rectangle — it is at least made of ground — but demoted, per the note above.
        if (TryWorldBounds(out var searchLo, out var searchHi) &&
            DigNavmesh.TryWalkableBounds(searchLo, searchHi, out lo, out hi))
            return true;

        // LAST: the map rectangle. Known crude — it is mostly empty square for a small zone — but
        // it is never wrong about orientation, only about where the middle is.
        return TryWorldBounds(out lo, out hi);
    }

    public static void ContentBounds(out Vector2 lo, out Vector2 hi)
    {
        float span = MathF.Max(1f, MapSpan);

        lo = new Vector2(1f, 1f);
        hi = new Vector2(1f + span, 1f + span);

        // FIRST CHOICE: the navmesh. It is the walkable area by definition, so its extent is the
        // extent of anywhere the player can be — which is exactly what a quadrant should divide.
        // Sansflaire's call, and it is better than both fallbacks: the coordinate range is a mostly-empty
        // square, and landmarks describe where the game puts LABELS, which cluster on features and
        // miss open ground.
        if (TryWorldBounds(out var worldLo, out var worldHi) &&
            DigNavmesh.TryWalkableBounds(worldLo, worldHi, out var walkLo, out var walkHi) &&
            TryMapCoords(new Vector3(walkLo.X, 0f, walkLo.Y), out float ax, out float ay) &&
            TryMapCoords(new Vector3(walkHi.X, 0f, walkHi.Y), out float bx, out float by))
        {
            lo = new Vector2(MathF.Min(ax, bx), MathF.Min(ay, by));
            hi = new Vector2(MathF.Max(ax, bx), MathF.Max(ay, by));
            return;
        }

        // SECOND CHOICE: the map's own labels. Crude — they sit on features rather than spanning
        // the walkable area — but far better than the raw coordinate square.
        var marks = ForCurrentMap();
        if (marks.Count < 4) return;

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var m in marks)
        {
            if (m.MapX < minX) minX = m.MapX;
            if (m.MapX > maxX) maxX = m.MapX;
            if (m.MapY < minY) minY = m.MapY;
            if (m.MapY > maxY) maxY = m.MapY;
        }

        // A degenerate spread means the markers are clustered and bound nothing useful.
        if (maxX - minX < 2f || maxY - minY < 2f) return;

        // Padded outward a little: markers sit ON features, so the walkable area extends past the
        // outermost of them. Without this the edge cells would start inside the content.
        float padX = (maxX - minX) * 0.10f;
        float padY = (maxY - minY) * 0.10f;

        lo = new Vector2(minX - padX, minY - padY);
        hi = new Vector2(maxX + padX, maxY + padY);
    }

    /// <summary>
    /// The inverse of <see cref="TryMapCoords"/> — a world position from map coordinates.
    ///
    /// <para>Algebra, not a second formula. <c>mapX = 41/c * (((wx + offX) * c + 1024) / 2048) + 1</c>
    /// rearranges to <c>wx = (((mapX - 1) * c / 41) * 2048 - 1024) / c - offX</c>. If the forward
    /// conversion is right this is right, and if it is wrong they are wrong together — which is the
    /// property that matters for dragging a box on the map: the corner lands where it was dropped
    /// relative to everything else drawn through the same transform.</para>
    /// </summary>
    public static bool TryWorldFromMap(float mapX, float mapY, out Vector3 world)
    {
        world = default;

        try
        {
            var maps = Plugin.DataManager.GetExcelSheet<LSheets.Map>();
            if (maps?.GetRowOrDefault(CurrentMapId()) is not { } map) return false;

            float c = MathF.Max(0.01f, map.SizeFactor / 100f);

            float wx = (((mapX - 1f) * c / 41f) * 2048f - 1024f) / c - map.OffsetX;
            float wz = (((mapY - 1f) * c / 41f) * 2048f - 1024f) / c - map.OffsetY;

            world = new Vector3(wx, 0f, wz);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Map coordinates for a world position — the numbers in the game's own readout.</summary>
    public static bool TryMapCoords(Vector3 world, out float mapX, out float mapY)
    {
        mapX = mapY = 0f;

        try
        {
            var maps = Plugin.DataManager.GetExcelSheet<LSheets.Map>();
            if (maps?.GetRowOrDefault(CurrentMapId()) is not { } map) return false;

            float c = MathF.Max(0.01f, map.SizeFactor / 100f);

            mapX = 41f / c * (((world.X + map.OffsetX) * c + 1024f) / 2048f) + 1f;
            mapY = 41f / c * (((world.Z + map.OffsetY) * c + 1024f) / 2048f) + 1f;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// The map's full extent in WORLD coordinates — the rectangle the drawn map covers.
    ///
    /// <para>Derived by running the verified world-to-map conversion backwards at the map's own
    /// edges. Map coordinates run from 1 to 1 + 41/c, which correspond to raw 0 and raw 2048, so the
    /// world edges are <c>±1024/c - offset</c>. Sampling this rectangle is what makes the WHOLE map
    /// eligible rather than a ring around wherever the player is standing.</para>
    /// </summary>
    public static bool TryWorldBounds(out Vector2 min, out Vector2 max)
    {
        min = max = default;

        try
        {
            var maps = Plugin.DataManager.GetExcelSheet<LSheets.Map>();
            if (maps?.GetRowOrDefault(CurrentMapId()) is not { } map) return false;

            float c = MathF.Max(0.01f, map.SizeFactor / 100f);
            float h = 1024f / c;

            min = new Vector2(-h - map.OffsetX, -h - map.OffsetY);
            max = new Vector2( h - map.OffsetX,  h - map.OffsetY);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Drops the cache, so a map edited or re-entered is re-read rather than remembered.</summary>
    public static void Invalidate()
    {
        _cachedMap = 0;

        // The walkable box is per-territory and is the primary source for ContentBounds, so a stale
        // one would keep quadrants pointing at the previous zone's geometry.
        DigNavmesh.InvalidateBounds();
    }
}
#endif
