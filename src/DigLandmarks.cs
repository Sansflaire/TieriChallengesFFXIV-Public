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
        if (!TryMapCoords(world, out float mx, out float my)) return "somewhere on this map";

        float span   = MathF.Max(1f, MapSpan);
        float centre = 1f + span * 0.5f;

        // Inside this fraction of the span from the centre line counts as neither side.
        const float Middle = 0.14f;
        float band = span * Middle;

        string ns = my < centre - band ? "NORTH" : my > centre + band ? "SOUTH" : string.Empty;
        string ew = mx > centre + band ? "EAST"  : mx < centre - band ? "WEST"  : string.Empty;

        if (ns.Length == 0 && ew.Length == 0) return "the middle of the map";
        if (ns.Length == 0) return $"the {ew} side of the map";
        if (ew.Length == 0) return $"the {ns} of the map";

        return $"the {ns}-{ew} of the map";
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

    /// <summary>Drops the cache, so a map edited or re-entered is re-read rather than remembered.</summary>
    public static void Invalidate() => _cachedMap = 0;
}
#endif
