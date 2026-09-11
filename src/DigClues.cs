#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Game.ClientState.Objects.Enums;

using LSheets = Lumina.Excel.Sheets;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. The kinds of thing a generated clue can be written ABOUT.
///
/// <para>Stored as bit positions in <see cref="DigTuning.RoamClueCategories"/>, so append only and
/// never renumber — a saved mask stores the raw bits.</para>
/// </summary>
[Flags]
internal enum ClueCategory
{
    None      = 0,
    Landmark  = 1 << 0,
    Aetheryte = 1 << 1,
    Section   = 1 << 2,
    Quadrant  = 1 << 3,
    Clock     = 1 << 4,
    Npc       = 1 << 5,
    Enemy     = 1 << 6,

    All = Landmark | Aetheryte | Section | Quadrant | Clock | Npc | Enemy,
}

/// <summary>
/// DEVELOPER BUILD ONLY. Writes the clue for one buried spot, choosing what KIND of clue to write by
/// weighted draw so a trail does not say the same thing five times.
///
/// <para><b>Anti-repeat is a weight, not a ban.</b> Each category starts at an equal share. Using one
/// multiplies its weight down by <see cref="DigTuning.RoamRepeatPenalty"/>; every clue written without
/// it recovers the weight toward its share. So a category that was just used is unlikely rather than
/// impossible, which is what was asked for and is also the only version that degrades properly — on a
/// map where only two categories can produce anything, a hard ban would deadlock on the third clue and
/// a weight simply lets the same one come round again.</para>
///
/// <para><b>A category that cannot describe this spot contributes nothing to the draw</b>, and that is
/// different from being suppressed. Availability is decided by actually trying to write the clue: the
/// candidate is built first and only then weighed. There is no predicate that could disagree with what
/// the writer would do, because the writer's own output is the predicate.</para>
///
/// <para><b>Clues are allowed to be awkward.</b> An anchor is not necessarily the NEAREST one — see
/// <see cref="PickAnchor"/>. "Far EAST of Ingleside Subdivision" when Ingleside is on the west side of
/// the map is a true statement, a worse clue, and deliberate.</para>
/// </summary>
internal sealed class DigClueWriter
{
    private readonly Random _rng;

    /// <summary>Equal starting share. The absolute number is arbitrary; only ratios are read.</summary>
    private const float BaseWeight = 100f;

    /// <summary>How fast a suppressed category climbs back per clue that did not use it.</summary>
    private const float Recovery = 1.9f;

    /// <summary>Live weight per category, keyed by bit position.</summary>
    private readonly Dictionary<ClueCategory, float> _weight = new();

    /// <summary>What the last <see cref="Write"/> actually chose, for the lab readout.</summary>
    public ClueCategory LastCategory { get; private set; }

    public DigClueWriter(Random rng)
    {
        _rng = rng;
        foreach (var c in Categories) _weight[c] = BaseWeight;
    }

    private static readonly ClueCategory[] Categories =
    {
        ClueCategory.Landmark, ClueCategory.Aetheryte, ClueCategory.Section,
        ClueCategory.Quadrant, ClueCategory.Clock, ClueCategory.Npc, ClueCategory.Enemy,
    };

    // ── the one public call ──────────────────────────────────────────────────

    /// <summary>
    /// A clue for <paramref name="spot"/>, and the weight bookkeeping that stops the next one being
    /// the same kind of clue.
    /// </summary>
    public string Write(Vector3 spot)
    {
        var enabled = (ClueCategory)DigTuning.RoamClueCategories;
        float hard  = Math.Clamp(DigTuning.RoamDifficulty, 0f, 1f);

        // Build every candidate BEFORE weighing any of them. Availability is "the writer produced
        // something", never a separate guess about whether it could have.
        var candidates = new List<(ClueCategory Cat, string Text)>();

        foreach (var cat in Categories)
        {
            if ((enabled & cat) == 0) continue;

            string text = Compose(cat, spot, hard);
            if (text.Length == 0) continue;

            candidates.Add((cat, text));
        }

        if (candidates.Count == 0)
        {
            // Nothing at all could be said about this position. The quadrant is the last resort and
            // is computed without any sheet, so this is genuinely the end of the line.
            LastCategory = ClueCategory.None;
            return $"Somewhere in {DigLandmarks.Quadrant(spot)}.";
        }

        var chosen = Draw(candidates);

        Suppress(chosen.Cat);
        LastCategory = chosen.Cat;

        return chosen.Text;
    }

    /// <summary>
    /// The clue ONE named category would write, ignoring the weights, the enable mask and the
    /// difficulty setting. Empty when that category has nothing to say about this spot.
    ///
    /// <para><b>For testing a category in isolation, which is the only honest way to judge one.</b>
    /// In a real trail a category competes in a draw, so a category that writes bad clues is easy to
    /// miss — it turns up once in five and reads as a one-off. Pinning it and spawning a single spot
    /// makes its output the whole of what you see.</para>
    ///
    /// <para>Deliberately does NOT touch the weights: a test spawn is not part of a trail and must
    /// not change what the next real trail draws.</para>
    /// </summary>
    public string WriteAs(ClueCategory only, Vector3 spot, float hard) =>
        Compose(only, spot, Math.Clamp(hard, 0f, 1f));

    /// <summary>Weighted draw over the categories that produced something.</summary>
    private (ClueCategory Cat, string Text) Draw(List<(ClueCategory Cat, string Text)> candidates)
    {
        float total = 0f;
        foreach (var c in candidates) total += MathF.Max(0.0001f, _weight[c.Cat]);

        float roll = (float)_rng.NextDouble() * total;

        foreach (var c in candidates)
        {
            roll -= MathF.Max(0.0001f, _weight[c.Cat]);
            if (roll <= 0f) return c;
        }

        return candidates[candidates.Count - 1];
    }

    /// <summary>
    /// Knocks the chosen category down and lets every other one climb back toward its share.
    /// Recovery is applied to all of them, including ones that were unavailable this time — a
    /// category the map cannot support is not being "saved up", and letting it accumulate an
    /// enormous weight would make it dominate the instant it did become available.
    /// </summary>
    private void Suppress(ClueCategory used)
    {
        float penalty = Math.Clamp(DigTuning.RoamRepeatPenalty, 0f, 0.99f);

        foreach (var c in Categories)
        {
            if (c == used) _weight[c] = MathF.Max(0.5f, _weight[c] * (1f - penalty));
            else           _weight[c] = MathF.Min(BaseWeight, _weight[c] * Recovery);
        }
    }

    // ── one composer per category ────────────────────────────────────────────

    /// <summary>
    /// The clue this category would write for this spot, or empty when it has nothing to say.
    /// Empty is the availability signal; there is no second path that decides it.
    /// </summary>
    private string Compose(ClueCategory cat, Vector3 spot, float hard) => cat switch
    {
        ClueCategory.Landmark  => FromAnchors(DigClueSources.Landmarks(), spot, hard, "named place"),
        ClueCategory.Aetheryte => FromAnchors(DigClueSources.Aetherytes(), spot, hard, "aetheryte"),
        ClueCategory.Section   => FromAnchors(DigClueSources.Sections(),   spot, hard, "map section"),
        ClueCategory.Npc       => FromAnchors(DigClueSources.NearbyNpcs(spot),    spot, hard, "npc"),
        ClueCategory.Enemy     => FromAnchors(DigClueSources.NearbyEnemies(spot), spot, hard, "enemy"),
        ClueCategory.Quadrant  => FromQuadrant(spot, hard),
        ClueCategory.Clock     => FromClock(spot, hard),
        _                      => string.Empty,
    };

    /// <summary>
    /// A clue of the form "&lt;direction&gt; of &lt;anchor&gt;", with how much else is given away
    /// decided by difficulty.
    ///
    /// <para><b>Difficulty is how much is WITHHELD, not how vague the words are.</b> Easy adds a
    /// distance word so the player knows roughly how far to walk. Medium drops the distance and adds
    /// the map quadrant, which narrows the search without pointing at it. Hard drops the bearing
    /// entirely and gives two anchors instead, so the position is an intersection the player has to
    /// work out on the map rather than a line they can walk along.</para>
    /// </summary>
    private string FromAnchors(IReadOnlyList<DigClueSources.Anchor> anchors, Vector3 spot,
                               float hard, string kind)
    {
        if (anchors.Count == 0) return string.Empty;

        var near = PickAnchor(anchors, spot, hard, out float dist, exclude: null);
        if (near is not { } a) return string.Empty;

        string quad = DigLandmarks.Quadrant(spot);

        // HARD — two anchors and no bearing at all.
        if (hard >= 0.67f && anchors.Count > 1)
        {
            var second = PickAnchor(anchors, spot, hard, out float secondDist, exclude: a.Name);

            if (second is { } b)
                return secondDist > dist
                    ? $"Between {a.Name} and {b.Name}, nearer the first. Somewhere in {quad}."
                    : $"Between {a.Name} and {b.Name}, nearer the second. Somewhere in {quad}.";
        }

        string bearing = $"{Intensity(dist)}{DigGround.Compass(a.World, spot)} of {a.Name}";

        // MEDIUM — bearing plus the quadrant, but no sense of how far to go.
        if (hard >= 0.34f) return $"{bearing}, in {quad}.";

        // EASY — bearing plus a distance word. Three of the four things needed.
        return $"{bearing}, {DigGround.Vagueness(dist)}.";
    }

    /// <summary>
    /// Which anchor to describe the spot from — <b>not necessarily the nearest one</b>.
    ///
    /// <para>An always-nearest anchor makes every clue the same shape and, worse, makes the whole
    /// trail solvable by walking to the named place and sweeping a small circle. Reaching further
    /// down the distance-ordered list produces clues that are still exactly true and considerably
    /// more annoying: "Far EAST of Ingleside Subdivision" is a real constraint even when Ingleside is
    /// at the other end of the map, and the player has to think about which of the several things
    /// they are east of actually narrows anything.</para>
    ///
    /// <para><b>How far down the list is capped by difficulty AND by
    /// <see cref="DigTuning.RoamAwkwardness"/>.</b> At awkwardness 0 it is always the nearest, which
    /// is the old behaviour and is kept reachable on purpose — if a generated trail turns out to be
    /// unsolvable, turning this to 0 is the first thing to try, and it splits "the clues are cruel"
    /// from "the clues are wrong" in one move.</para>
    /// </summary>
    private DigClueSources.Anchor? PickAnchor(IReadOnlyList<DigClueSources.Anchor> anchors,
                                              Vector3 spot, float hard, out float distance,
                                              string? exclude)
    {
        distance = 0f;

        // Distance-ordered, nearest first. A small list, and it is rebuilt per clue rather than
        // cached because the exclusion differs between the two anchors of a hard clue.
        var ranked = new List<(DigClueSources.Anchor A, float D)>();

        foreach (var a in anchors)
        {
            if (exclude != null && string.Equals(a.Name, exclude, StringComparison.Ordinal)) continue;
            ranked.Add((a, DigGround.Flat(a.World, spot)));
        }

        if (ranked.Count == 0) return null;

        ranked.Sort((x, y) => x.D.CompareTo(y.D));

        float awkward = Math.Clamp(DigTuning.RoamAwkwardness, 0f, 1f);

        // The window of ranks the draw may reach into. Grows with both knobs, and is always at least
        // 1 so awkwardness 0 means "the nearest, every time".
        int window = 1 + (int)MathF.Round(awkward * (0.35f + 0.65f * hard) * (ranked.Count - 1));
        window = Math.Clamp(window, 1, ranked.Count);

        // Biased toward the near end even inside the window: squaring the roll makes a distant
        // anchor the occasional twist rather than the norm, which keeps a trail solvable.
        float u    = (float)_rng.NextDouble();
        int   rank = (int)(u * u * window);
        rank = Math.Clamp(rank, 0, window - 1);

        distance = ranked[rank].D;
        return ranked[rank].A;
    }

    /// <summary>
    /// A word in front of the bearing when the anchor is a long way off, so a deliberately distant
    /// anchor reads as intentional rather than as a mistake.
    /// </summary>
    private static string Intensity(float d) => d switch
    {
        >= 260f => "Far, far ",
        >= 140f => "Far ",
        >= 70f  => "Well ",
        _       => string.Empty,
    };

    /// <summary>
    /// The spot located purely by where it falls on the map grid. No anchor, so this works on maps
    /// that have no named anything — which is the reason it exists.
    /// </summary>
    private string FromQuadrant(Vector3 spot, float hard)
    {
        if (!DigLandmarks.TryMapCoords(spot, out float mx, out float my)) return string.Empty;

        string quad = DigLandmarks.Quadrant(spot);

        // EASY — the quadrant plus which third of it, which is a genuinely small box.
        if (hard < 0.34f)
        {
            float span = MathF.Max(1f, DigLandmarks.MapSpan);
            int   col  = Math.Clamp((int)((mx - 1f) / span * 3f), 0, 2);
            int   row  = Math.Clamp((int)((my - 1f) / span * 3f), 0, 2);

            return $"In {quad} — the {Thirds[row]} {Thirds[col]} of the map, by the grid.";
        }

        // MEDIUM — the quadrant alone.
        if (hard < 0.67f) return $"Somewhere in {quad}.";

        // HARD — a half rather than a quadrant, which is half the map.
        bool vertical = _rng.Next(2) == 0;
        float centre  = 1f + MathF.Max(1f, DigLandmarks.MapSpan) * 0.5f;

        return vertical
            ? $"In the {(my < centre ? "NORTHERN" : "SOUTHERN")} half of the map. That is all anyone will say."
            : $"In the {(mx > centre ? "EASTERN" : "WESTERN")} half of the map. That is all anyone will say.";
    }

    private static readonly string[] Thirds = { "upper", "middle", "lower" };

    /// <summary>
    /// The spot as a clock bearing from the middle of the map, plus how far out it sits.
    ///
    /// <para><b>Worked in MAP coordinates and not world ones</b>, same as the quadrant: the player
    /// reads this off the map screen, where 12 o'clock is straight up and straight up is north.</para>
    /// </summary>
    private string FromClock(Vector3 spot, float hard)
    {
        if (!DigLandmarks.TryMapCoords(spot, out float mx, out float my)) return string.Empty;

        float span   = MathF.Max(1f, DigLandmarks.MapSpan);
        float centre = 1f + span * 0.5f;

        float east  = mx - centre;
        float north = centre - my;          // map Y increases downward, so north is the negative side

        float radius = MathF.Sqrt(east * east + north * north);

        // Dead centre has no bearing to give. Saying "12 o'clock" for a spot two yalms from the
        // middle would send the player confidently to the wrong end of the map.
        if (radius < span * 0.06f) return "Right at the heart of the map.";

        float bearing = MathF.Atan2(east, north) * 180f / MathF.PI;
        if (bearing < 0f) bearing += 360f;

        int hour = (int)MathF.Round(bearing / 30f) % 12;
        if (hour == 0) hour = 12;

        // How far out, as a fraction of the distance from the middle to the corner.
        float reach = Math.Clamp(radius / (span * 0.5f), 0f, 1.5f);

        string howFar = reach switch
        {
            >= 0.78f => "right out at the edge",
            >= 0.45f => "about two-thirds of the way out",
            _        => "not far from the middle",
        };

        // HARD — the bearing alone, which is a line from the centre and not a point.
        if (hard >= 0.67f) return $"{hour} o'clock from the middle of the map. Follow the line.";

        // MEDIUM — bearing and reach.
        if (hard >= 0.34f) return $"{hour} o'clock from the middle of the map, {howFar}.";

        // EASY — bearing, reach and the quadrant to confirm it.
        return $"{hour} o'clock from the middle of the map, {howFar} — {DigLandmarks.Quadrant(spot)}.";
    }
}

/// <summary>
/// DEVELOPER BUILD ONLY. Where the named things a clue can point at come from.
///
/// <para>Every source here is either a game sheet or the live object table. <b>Nothing is hand-listed
/// and nothing is scraped</b>, which is what makes a generated clue use the game's own words for a
/// place rather than ours.</para>
/// </summary>
internal static class DigClueSources
{
    /// <summary>Something with a name and a world position that a clue can be written from.</summary>
    internal readonly record struct Anchor(string Name, Vector3 World);

    /// <summary>Everything the game draws a NAME for on this map. The broadest source.</summary>
    public static IReadOnlyList<Anchor> Landmarks()
    {
        var list = new List<Anchor>();
        foreach (var l in DigLandmarks.ForCurrentMap()) list.Add(new Anchor(l.Name, l.World));
        return list;
    }

    /// <summary>
    /// The aetherytes in the player's current territory, positioned from the <c>Level</c> row each
    /// one points at.
    ///
    /// <para><b>This is a separate source from the landmarks on purpose, even though an aetheryte
    /// plaza usually also has a map label.</b> An aetheryte is the one landmark every player can
    /// teleport to and therefore the one anchor that is always findable — "north of the Ingleside
    /// aetheryte" is actionable in a way that "north of some carved rock" is not. Keeping it its own
    /// category is what lets the weighted draw guarantee some of the trail is easy.</para>
    ///
    /// <para><b>Aethernet shards count, and excluding them was wrong (0.84.44.2).</b> The first cut
    /// filtered on <c>IsAetheryte</c>, reasoning that a shard's name duplicates a district label that
    /// is already a landmark. In a city that is arguable; <b>in a housing ward it is backwards</b> —
    /// the subdivision shards are the only teleport anchors there, so filtering them left Empyreum
    /// reporting zero aetherytes while standing next to several. A shard is exactly what this
    /// category is for: a named point a player can teleport to and navigate from.</para>
    /// </summary>
    public static IReadOnlyList<Anchor> Aetherytes()
    {
        if (_aetheryteTerritory == Plugin.ClientState.TerritoryType && _aetherytes != null)
            return _aetherytes;

        _aetherytes         = Load(out _aetheryteReport);
        _aetheryteTerritory = Plugin.ClientState.TerritoryType;

        return _aetherytes;
    }

    private static uint          _aetheryteTerritory = uint.MaxValue;
    private static List<Anchor>? _aetherytes;
    private static string        _aetheryteReport = "not read yet.";

    /// <summary>
    /// Why the aetheryte list came out the size it did — every candidate row and what happened to
    /// it. Purely diagnostic, and it exists because "this zone has no aetherytes" and "every row was
    /// rejected by a filter" produce the identical count of zero.
    /// </summary>
    public static string AetheryteReport { get { Aetherytes(); return _aetheryteReport; } }

    private static List<Anchor> Load(out string report)
    {
        var list  = new List<Anchor>();
        var lines = new List<string>();

        int seen = 0, noName = 0, noLevel = 0;

        try
        {
            uint territory = Plugin.ClientState.TerritoryType;
            var  sheet     = Plugin.DataManager.GetExcelSheet<LSheets.Aetheryte>();

            if (sheet != null)
            {
                foreach (var a in sheet)
                {
                    if (a.Territory.RowId != territory) continue;

                    seen++;

                    string place = a.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                    string shard = a.AethernetName.ValueNullable?.Name.ExtractText() ?? string.Empty;

                    // A shard's own name is the useful one — "Halberd's Head Subdivision" rather
                    // than the ward it hangs off. Falls back to the place name when it has none,
                    // which is what a full aetheryte carries.
                    string name = a.IsAetheryte || shard.Length == 0 ? place : shard;

                    if (name.Length == 0) { noName++; lines.Add($"  row {a.RowId}: no name"); continue; }

                    // The Level row carries the real world position. One with no level row is
                    // skipped rather than placed at the origin, which would anchor a clue to the
                    // middle of nowhere with complete confidence.
                    //
                    // NOTE: the level's own Territory is deliberately NOT re-checked. The Aetheryte
                    // row is already filtered by territory, so its levels are in it by construction,
                    // and demanding the match twice rejected every row whose Level.Territory is
                    // unset — which is how this list came back empty in a housing ward.
                    Vector3? found = null;

                    foreach (var lvlRef in a.Level)
                    {
                        if (lvlRef.ValueNullable is not { } lvl) continue;
                        if (lvl.X == 0f && lvl.Y == 0f && lvl.Z == 0f) continue;

                        found = new Vector3(lvl.X, lvl.Y, lvl.Z);
                        break;
                    }

                    if (found is not { } pos)
                    {
                        noLevel++;
                        lines.Add($"  row {a.RowId} \"{name}\": no usable Level row");
                        continue;
                    }

                    string label = a.IsAetheryte
                        ? $"the {name} aetheryte"
                        : $"the {name} aethernet shard";

                    list.Add(new Anchor(label, pos));
                    lines.Add($"  row {a.RowId} {(a.IsAetheryte ? "AETHERYTE" : "shard")} "
                            + $"\"{name}\" at ({pos.X:0.0}, {pos.Z:0.0})");
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Clue] aetheryte read failed: {ex.Message}");
            lines.Add($"  read failed: {ex.Message}");
        }

        report = $"Aetheryte sheet rows for this territory: {seen}. "
               + $"Usable {list.Count}, no name {noName}, no position {noLevel}.\n"
               + string.Join("\n", lines);

        return list;
    }

    /// <summary>
    /// The map labels that name a SECTION of the map rather than a point in it — subdivisions, wards,
    /// districts, quarters, plazas.
    ///
    /// <para><b>This is a word heuristic over the game's own vocabulary, and it is allowed to find
    /// nothing.</b> There is no sheet column that says "this label is an area rather than a
    /// landmark"; the distinction lives in the words Square Enix chose. Matching those words is
    /// therefore a guess about English naming, not about data — and when it guesses wrong the failure
    /// is a clue anchored to a slightly odd place, not a clue anchored somewhere untrue. On a map
    /// with no such labels the category simply abstains and the weight goes elsewhere.</para>
    /// </summary>
    public static IReadOnlyList<Anchor> Sections()
    {
        var list = new List<Anchor>();

        foreach (var l in DigLandmarks.ForCurrentMap())
        {
            bool isSection = false;

            foreach (var word in SectionWords)
            {
                if (l.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) continue;
                isSection = true;
                break;
            }

            if (isSection) list.Add(new Anchor(l.Name, l.World));
        }

        return list;
    }

    private static readonly string[] SectionWords =
        { "Subdivision", "Ward", "District", "Quarter", "Plaza", "Square", "Market", "Gate" };

    /// <summary>
    /// Named friendly NPCs the client currently has loaded near a position.
    ///
    /// <para><b>This only sees what the game has streamed in, and that is a real limitation rather
    /// than a bug to fix later.</b> The object table holds objects near the PLAYER, so a spot on the
    /// far side of the map has no NPCs in it as far as this can tell, and the category abstains for
    /// that spot. It is honest — an NPC clue is written only when there genuinely was an NPC there at
    /// the moment the trail was generated. The offline alternative is the <c>Level</c> sheet, which
    /// would cover the whole map but needs its <c>Type</c> enum confirmed before its rows can be
    /// trusted to be NPCs; that is a research item, not a guess to make here.</para>
    /// </summary>
    public static IReadOnlyList<Anchor> NearbyNpcs(Vector3 spot) =>
        NearbyOfKind(spot, ObjectKind.EventNpc);

    /// <summary>
    /// Named hostile creatures the client currently has loaded near a position. Same streaming
    /// limitation as <see cref="NearbyNpcs"/>.
    ///
    /// <para><b>A mob clue names a KIND of creature, not an individual</b>, and the phrasing in the
    /// clue writer reflects that — the specific wharf rat standing there when the trail was generated
    /// will have wandered off or been killed by the time the player arrives, but wharf rats in
    /// general will still be roughly where wharf rats live.</para>
    /// </summary>
    public static IReadOnlyList<Anchor> NearbyEnemies(Vector3 spot) =>
        NearbyOfKind(spot, ObjectKind.BattleNpc);

    /// <summary>How far from a spot a loaded object may be and still be called "there".</summary>
    private const float NearRadius = 55f;

    private static IReadOnlyList<Anchor> NearbyOfKind(Vector3 spot, ObjectKind kind)
    {
        var list = new List<Anchor>();

        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj == null || obj.ObjectKind != kind) continue;

                string name = obj.Name.TextValue;
                if (name.Length == 0) continue;

                if (DigGround.Flat(obj.Position, spot) > NearRadius) continue;

                // One entry per NAME. Six identical rats around a spot are one anchor, and the clue
                // that names them is about the species rather than about any one of them.
                if (!seen.Add(name)) continue;

                list.Add(new Anchor(kind == ObjectKind.BattleNpc ? $"the {name}s" : name, obj.Position));
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Clue] object table read failed: {ex.Message}");
        }

        return list;
    }

    /// <summary>Drops the per-territory caches, so a zone change re-reads rather than remembering.</summary>
    public static void Invalidate()
    {
        _aetheryteTerritory = uint.MaxValue;
        _aetherytes         = null;
        _aetheryteReport    = "not read yet.";
    }

    /// <summary>
    /// What each category can actually offer on this map right now, for the lab. Purely diagnostic:
    /// "the clues are all quadrants" and "only the quadrant category had anything to say" look
    /// identical from the player's side, and this tells them apart in one click.
    /// </summary>
    public static string Census(Vector3 around)
    {
        return $"landmarks {Landmarks().Count}   aetherytes+shards {Aetherytes().Count}   "
             + $"sections {Sections().Count}   npcs near you {NearbyNpcs(around).Count}   "
             + $"enemies near you {NearbyEnemies(around).Count}";
    }
}
#endif
