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

    /// <summary>
    /// Every phrase a generated clue can contain, and precisely what each one means.
    ///
    /// <para><b>Derived by CALLING the real helpers, not by transcribing them.</b> The compass
    /// points come from <see cref="DigGround.Compass"/> fed synthetic offsets, the distance words
    /// from <see cref="DigGround.Vagueness"/> fed representative distances, the intensity prefixes
    /// from <see cref="Intensity"/>, and the grid cells from the same <see cref="Cells"/> array the
    /// writer indexes. A glossary that restated these would be wrong the first time a threshold
    /// moved, and wrong silently — which is the failure mode this whole file has spent the day
    /// on.</para>
    ///
    /// <para>The numeric boundaries are the one part that IS written down, because they live inside
    /// switch arms rather than in named constants. They are stated next to the phrase they produce
    /// so a mismatch is visible rather than buried.</para>
    /// </summary>
    public static string[] Vocabulary()
    {
        var lines = new List<string>();

        lines.Add("HOW DIFFICULTY WORKS — two dials in one slider.");
        lines.Add("  COUNT steps at the band boundaries. A clue states exactly this many facts:");
        lines.Add("    EASY   (0.00-0.33)   3 facts");
        lines.Add("    MEDIUM (0.34-0.66)   2 facts");
        lines.Add("    HARD   (0.67-1.00)   1 fact");
        lines.Add("  PRECISION varies WITHIN each band, using the rest of the slider's travel. An");
        lines.Add("  easy-easy clue gives a bearing to one of eight points and a distance in yalms;");
        lines.Add("  a hard-easy clue gives the same three facts as one of four quarters and a");
        lines.Add("  woolly distance word. Fewer facts and vaguer facts are different levers.");
        lines.Add("  Facts are always dropped from the LEAST useful end, so a HARD clue is the single");
        lines.Add("  most actionable thing that could have been said - never an arbitrary leftover.");
        lines.Add("");
        lines.Add("DIRECTION — world axes, NOT relative to your facing. +X is east, +Z is south.");
        lines.Add("  Eight points, each covering 45 degrees, so \"NORTH\" means within 22.5 of due north:");

        // Fed real offsets so the words are the writer's own, whatever they are.
        var probes = new (float X, float Z, string Where)[]
        {
            (0, -1, "due north"), (1, -1, "north-east"), (1, 0, "due east"),  (1, 1, "south-east"),
            (0,  1, "due south"), (-1, 1, "south-west"), (-1, 0, "due west"), (-1, -1, "north-west"),
        };

        foreach (var (x, z, where) in probes)
            lines.Add($"    {DigGround.Compass(Vector3.Zero, new Vector3(x, 0f, z)),-12} = {where}");

        lines.Add("");
        lines.Add("HOW FAR — appended on EASY clues only. Straight-line, not walking distance:");

        foreach (float d in new[] { 20f, 60f, 130f, 250f })
            lines.Add($"    \"{DigGround.Vagueness(d)}\"".PadRight(26) + $"~{d:0} yalms");

        lines.Add("");
        lines.Add("INTENSITY — prefixes the direction when the anchor is far from the spot.");
        lines.Add("  It describes the ANCHOR's distance, not how far you must walk:");

        foreach (float d in new[] { 40f, 90f, 180f, 300f })
        {
            string p = Intensity(d);
            lines.Add($"    \"{(p.Length == 0 ? "(none)" : p.Trim())}\"".PadRight(26) + $"anchor ~{d:0} yalms away");
        }

        lines.Add("");
        lines.Add("MAP REGION — measured against the WALKABLE BOX, not the map image.");
        lines.Add("  A central band of 14% per axis counts as neither side, so \"the middle of the");
        lines.Add("  map\" is a real answer and not a rounding artefact:");
        lines.Add("    \"the NORTH of the map\"          north band, neither east nor west");
        lines.Add("    \"the EAST side of the map\"      east band, neither north nor south");
        lines.Add("    \"the NORTH-EAST of the map\"     both, i.e. a corner quarter");
        lines.Add("    \"the middle band of the map\"    inside the central band on BOTH axes");
        lines.Add("");
        lines.Add("EVERY REGION PHRASE NAMES AN AREA, NEVER A POINT.");
        lines.Add("  \"Dead in the very centre\" used to be said for a region over a hundred yalms");
        lines.Add("  across. A player who walks to the exact middle and digs finds nothing and");
        lines.Add("  concludes the clue lied — so the words now say \"ninth\", \"third\" or \"band\",");
        lines.Add("  and the EASY grid clue states the region's size in yalms outright.");
        lines.Add("");
        lines.Add("GRID CELL — EASY quadrant clues only. The walkable box cut into nine:");

        foreach (string c in Cells) lines.Add($"    \"{c}\"");

        lines.Add("");
        lines.Add("CLOCK — bearing from the CENTRE of the walkable box. 12 is north.");
        lines.Add("    \"N o'clock from the middle of the map\"   a line out from the centre");
        lines.Add("    \"right out at the edge\"                  >78% of the way to the rim");
        lines.Add("    \"about two-thirds of the way out\"        45-78%");
        lines.Add("    \"not far from the middle\"                <45%");
        lines.Add("    \"Near the middle of the map — within\"    within 6% of the centre; the yalm");
        lines.Add("    \"about N yalms of dead centre\"           figure is stated, no bearing given");
        lines.Add("");
        lines.Add("NEGATIVE — rules out a circle rather than pointing along a line.");
        lines.Add("    \"A long way from X - look in Q\"   X is 90+ yalms away. NEVER issued alone:");
        lines.Add("                                      everywhere is far from something, so it is");
        lines.Add("                                      always paired with a region.");
        lines.Add("");
        lines.Add("INTERSECTION — HARD clues. No bearing at all:");
        lines.Add("    \"Between A and B, nearer the first\"   you work it out on the map");
        lines.Add("");
        lines.Add("ANCHOR NAMING — how each source phrases itself:");
        lines.Add("    \"the X aetheryte\"                 a full aetheryte, from the Aetheryte sheet");
        lines.Add("    \"the X aethernet shard\"           a shard, incl. housing subdivisions");
        lines.Add("    \"the Aethernet Shard in Y\"        a LIVE shard object, qualified by section");
        lines.Add("    \"the Xs\" (plural)                 an enemy GROUP centroid, 2 or more");
        lines.Add("    \"the X\"  (singular)               one enemy, or a live world object");
        lines.Add("    bare name                          a map marker, an NPC placement, a section");
        lines.Add("");
        lines.Add("WHAT A CLUE NEVER CONTAINS: coordinates. A coordinate is not a clue, it is the");
        lines.Add("answer. Difficulty changes how much is WITHHELD, never how vague the words are.");

        return lines.ToArray();
    }

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
        ClueCategory.Aetheryte => FromAnchors(DigClueSources.AetherytesNear(spot), spot, hard, "aetheryte"),
        ClueCategory.Section   => FromAnchors(DigClueSources.Sections(),   spot, hard, "map section"),
        ClueCategory.Npc       => FromAnchors(DigClueSources.NearbyNpcs(spot),    spot, hard, "npc"),
        ClueCategory.Enemy     => FromAnchors(DigClueSources.NearbyEnemies(spot), spot, hard, "enemy"),
        ClueCategory.Quadrant  => FromQuadrant(spot, hard),
        ClueCategory.Clock     => FromClock(spot, hard),
        _                      => string.Empty,
    };

    /// <summary>
    /// How many separate facts a clue may state, and how sharply each one is allowed to be put.
    ///
    /// <para><b>Difficulty is two dials in one slider, and they do different jobs.</b> Sansflaire's model:
    /// EASY gives three pieces of information, MEDIUM two, HARD one — that is the COUNT, and it
    /// steps at the band boundaries. Within a band the slider still has a third of its travel to
    /// spend, and it spends it on PRECISION: an easy-easy clue names a bearing to one of eight
    /// points and a distance in yalms, while a hard-easy clue gives the same three facts as one of
    /// four quarters and a woolly distance word.</para>
    ///
    /// <para><b>Fewer facts and vaguer facts are not the same lever</b>, and conflating them is what
    /// the old code did: every category invented its own shape, so "hard" meant two landmarks in one
    /// place, no distance in another, and a coarser region in a third. A clue could get harder in a
    /// way that made it longer. Counting facts makes the difficulty of a clue legible from the clue
    /// itself — you can see three things, or one.</para>
    ///
    /// <para>Returns the count, and <c>sharp</c> as 0 at the easy end of the band through 1 at its
    /// hard end.</para>
    /// </summary>
    private static (int Count, float Sharp) Budget(float hard)
    {
        hard = Math.Clamp(hard, 0f, 1f);

        if (hard < 0.34f) return (3, hard / 0.34f);
        if (hard < 0.67f) return (2, (hard - 0.34f) / 0.33f);

        return (1, Math.Clamp((hard - 0.67f) / 0.33f, 0f, 1f));
    }

    /// <summary>
    /// One fact: a direction from an anchor, put more or less precisely.
    /// </summary>
    private static string DirectionFact(DigClueSources.Anchor a, Vector3 spot, float dist, float sharp)
    {
        // Eight points at the easy end, four at the hard end. Same statement, half the resolution.
        string bearing = sharp < 0.6f
            ? DigGround.Compass(a.World, spot)
            : DigGround.Compass4(a.World, spot);

        return $"{Intensity(dist)}{bearing} of {a.Name}";
    }

    /// <summary>One fact: how far, in yalms when sharp, as a word when not.</summary>
    private static string DistanceFact(float dist, float sharp)
    {
        // A number is a much stronger fact than a word, so it belongs at the easy end only. Rounded
        // to ten yalms because a clue is not a coordinate — "about 40 yalms" is a search radius,
        // "41.7 yalms" is the answer.
        if (sharp < 0.4f) return $"about {MathF.Round(dist / 10f) * 10f:0} yalms away";

        return DigGround.Vagueness(dist);
    }

    /// <summary>One fact: which part of the map, at one of three resolutions.</summary>
    private string RegionFact(Vector3 spot, float sharp)
    {
        if (!DigLandmarks.WalkableBox(out var lo, out var hi))
            return DigLandmarks.Quadrant(spot);

        // Sharpest: the ninth, with its size. Middle: the quadrant. Coarsest: a half, which is half
        // the map and barely narrows anything — correct for the hard end of a band.
        if (sharp < 0.34f)
        {
            float w = MathF.Max(0.01f, hi.X - lo.X);
            float d = MathF.Max(0.01f, hi.Y - lo.Y);

            int col = Math.Clamp((int)((spot.X - lo.X) / w * 3f), 0, 2);
            int row = Math.Clamp((int)((spot.Z - lo.Y) / d * 3f), 0, 2);

            float across = MathF.Round((w / 3f + d / 3f) * 0.5f / 5f) * 5f;

            return $"in {Cells[row * 3 + col]} (about {across:0} yalms across)";
        }

        if (sharp < 0.67f) return $"in {DigLandmarks.Quadrant(spot)}";

        float cx = (lo.X + hi.X) * 0.5f;
        float cz = (lo.Y + hi.Y) * 0.5f;

        return _rng.Next(2) == 0
            ? $"in the {(spot.Z < cz ? "NORTHERN" : "SOUTHERN")} half of the map"
            : $"in the {(spot.X > cx ? "EASTERN" : "WESTERN")} half of the map";
    }

    /// <summary>Assembles the chosen facts into one sentence.</summary>
    private static string Sentence(List<string> facts, int count)
    {
        if (facts.Count == 0) return string.Empty;

        var kept = facts.GetRange(0, Math.Min(count, facts.Count));

        string joined = string.Join(", ", kept);
        return char.ToUpperInvariant(joined[0]) + joined.Substring(1) + ".";
    }

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

        var (count, sharp) = Budget(hard);

        // Built in DESCENDING usefulness, then truncated to the budget. That ordering is the whole
        // design: dropping facts from the end removes the least useful ones first, so a HARD clue is
        // the single most actionable thing that could have been said rather than an arbitrary
        // survivor. The old code chose a different SHAPE per band, which meant harder clues
        // sometimes contained more words and occasionally more information.
        var facts = new List<string>();

        // FAR-FROM replaces the direction fact rather than adding to it: it is a statement about the
        // same anchor, so having both would spend two facts on one relationship. Only when the
        // anchor really is distant, or it would simply be false.
        bool awkward = dist >= 90f
                    && (float)_rng.NextDouble() < Math.Clamp(DigTuning.RoamAwkwardness, 0f, 1f) * 0.45f;

        facts.Add(awkward ? $"a long way from {a.Name}" : DirectionFact(a, spot, dist, sharp));

        // A negative constraint alone says nothing — everywhere is far from something — so when it
        // is used, the region comes next and is the fact that makes the pair solvable.
        if (awkward) facts.Add(RegionFact(spot, sharp));
        else         facts.Add(DistanceFact(dist, sharp));

        // The second anchor, when there is one. An intersection is a genuinely different fact from a
        // bearing, which is why it is offered rather than the region here.
        if (!awkward && anchors.Count > 1 && count >= 3)
        {
            var second = PickAnchor(anchors, spot, hard, out float secondDist, exclude: a.Name);

            facts.Add(second is { } b
                ? $"{DigGround.Compass(b.World, spot)} of {b.Name} as well"
                : RegionFact(spot, sharp));
        }
        else if (!awkward)
        {
            facts.Add(RegionFact(spot, sharp));
        }

        return Sentence(facts, count);
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
        // WORLD space throughout, matching Quadrant. Working in map coordinates here while the
        // quadrant worked in world coordinates would let the two clauses of one clue disagree.
        if (!DigLandmarks.WalkableBox(out var lo, out var hi)) return string.Empty;

        var (count, sharp) = Budget(hard);

        // This category has no anchor, so its facts are all positional. Descending usefulness: the
        // region first, then a bearing from the centre, then how far out.
        var facts = new List<string>
        {
            RegionFact(spot, sharp),
            ClockFact(spot, lo, hi, sharp),
            ReachFact(spot, lo, hi),
        };

        return Sentence(facts, count);
    }

    /// <summary>One fact: a clock bearing from the middle of the walkable box, 12 being north.</summary>
    private static string ClockFact(Vector3 spot, Vector2 lo, Vector2 hi, float sharp)
    {
        float cx = (lo.X + hi.X) * 0.5f;
        float cz = (lo.Y + hi.Y) * 0.5f;

        float east  = spot.X - cx;
        float north = cz - spot.Z;              // +Z is south, so north is the negative side

        if (MathF.Sqrt(east * east + north * north) < 1f) return "right around the middle";

        float bearing = MathF.Atan2(east, north) * 180f / MathF.PI;
        if (bearing < 0f) bearing += 360f;

        // Twelve hours at the easy end, quarters at the hard end — the same degradation the compass
        // fact uses, so the two read as the same kind of statement at the same difficulty.
        if (sharp >= 0.6f)
            return $"{DigGround.Compass4(new Vector3(cx, 0f, cz), spot)} of the map's middle";

        int hour = (int)MathF.Round(bearing / 30f) % 12;
        if (hour == 0) hour = 12;

        return $"{hour} o'clock from the middle of the map";
    }

    /// <summary>One fact: how far out from the centre, as a fraction of the way to the rim.</summary>
    private static string ReachFact(Vector3 spot, Vector2 lo, Vector2 hi)
    {
        float cx   = (lo.X + hi.X) * 0.5f;
        float cz   = (lo.Y + hi.Y) * 0.5f;
        float span = MathF.Max(1f, MathF.Max(hi.X - lo.X, hi.Y - lo.Y));

        float dx = spot.X - cx, dz = spot.Z - cz;
        float reach = Math.Clamp(MathF.Sqrt(dx * dx + dz * dz) / (span * 0.5f), 0f, 1.5f);

        return reach switch
        {
            >= 0.78f => "right out toward the edge",
            >= 0.45f => "about two-thirds of the way out",
            _        => "not far out from the middle",
        };
    }

    /// <summary>
    /// The nine cells of a 3x3 grid over the map, row-major from the north-west.
    ///
    /// <para><b>A flat table of nine, not two word lists indexed by row and column.</b> The composed
    /// version shipped "the middle middle of the map", because the vertical vocabulary
    /// ("upper/middle/lower") was indexed by the horizontal axis too — a mistake that is invisible
    /// in the code and obvious in the output. Nine strings cannot be composed wrongly, and each one
    /// is written the way a person would actually say it rather than assembled from parts that only
    /// read well on the diagonal.</para>
    /// </summary>
    /// <summary>
    /// <b>Every one of these names a REGION, and none of them names a point.</b>
    ///
    /// <para>The previous set said "very centre", and the clue read "Dead in the very centre of the
    /// map" — which to anyone reading it means the exact middle, to the yalm. A cell is one ninth of
    /// the walkable box and can be over a hundred yalms across, so the phrase promised a precision
    /// the clue does not have and cannot have. A player who walks to the exact centre and digs finds
    /// nothing, and correctly concludes the clue lied.</para>
    ///
    /// <para>"Ninth" is the honest word: it is accurate — a three-by-three grid really does make
    /// ninths — and it cannot be mistaken for a point. The centre cell is the one that most needed
    /// it and is now the most explicit of the nine.</para>
    /// </summary>
    private static readonly string[] Cells =
    {
        "the north-west ninth of the map",
        "the northern third of the map, midway between east and west",
        "the north-east ninth of the map",
        "the western third of the map, midway between north and south",
        "the middle ninth of the map — the central third both ways, not the exact centre",
        "the eastern third of the map, midway between north and south",
        "the south-west ninth of the map",
        "the southern third of the map, midway between east and west",
        "the south-east ninth of the map",
    };

    /// <summary>
    /// The spot as a clock bearing from the middle of the map, plus how far out it sits.
    ///
    /// <para><b>Worked in MAP coordinates and not world ones</b>, same as the quadrant: the player
    /// reads this off the map screen, where 12 o'clock is straight up and straight up is north.</para>
    /// </summary>
    private string FromClock(Vector3 spot, float hard)
    {
        if (!DigLandmarks.WalkableBox(out var lo, out var hi)) return string.Empty;

        var (count, sharp) = Budget(hard);

        // Same three facts as the quadrant category, reordered: the bearing leads here because it is
        // what makes this category distinct, and the region is the backstop.
        var facts = new List<string>
        {
            ClockFact(spot, lo, hi, sharp),
            ReachFact(spot, lo, hi),
            RegionFact(spot, sharp),
        };

        return Sentence(facts, count);
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

    /// <summary>
    /// Aetheryte anchors from the sheet PLUS the aethernet shards the client has loaded right now.
    ///
    /// <para><b>A housing subdivision's shard is in NO sheet this can reach.</b> Empyreum's shards
    /// are <c>EventObj</c> entries in the object table named "Aethernet Shard" — not <c>EventNpc</c>,
    /// not <c>BattleNpc</c>, and with no <c>Aetheryte</c> row bound to the territory. So every source
    /// here was blind to the single most obvious landmark in the zone, while the character stood next
    /// to one. Only looking at what the game actually had loaded found it.</para>
    ///
    /// <para><b>The live half is cached per call site, not per territory</b>, because object loading
    /// changes as the player moves — unlike the sheet half, which cannot.</para>
    /// </summary>
    public static IReadOnlyList<Anchor> AetherytesNear(Vector3 spot)
    {
        var list = new List<Anchor>(Aetherytes());

        // The housing shards, which are a different sheet entirely — see HousingShards.
        foreach (var h in HousingShards()) list.Add(h);

        foreach (var shard in LiveShards(spot))
        {
            // Sheet first: if the sheet already names this place, its name is the better one and the
            // generic "Aethernet Shard" would just be a duplicate anchor under a worse label.
            bool duplicate = false;

            foreach (var a in list)
            {
                if (DigGround.Flat(a.World, shard.World) > 12f) continue;
                duplicate = true;
                break;
            }

            if (!duplicate) list.Add(shard);
        }

        return list;
    }

    /// <summary>
    /// The aethernet shards of a housing district, from the <c>HousingAethernet</c> sheet.
    ///
    /// <para><b>They are not in the <c>Aetheryte</c> sheet and never were.</b> Two versions were
    /// spent loosening filters over that sheet — both fixed real defects, neither touched this,
    /// because a housing district's shards live in their own table. Empyreum has nine and the
    /// <c>Aetheryte</c> query found zero; that was not a filter being strict, it was the wrong
    /// table.</para>
    ///
    /// <para><b>This is the source that covers the whole map</b>, which the object table cannot: the
    /// client only streams in objects near the player, so exactly one of Empyreum's nine shards was
    /// visible as an <c>EventObj</c> at any time. A sheet has all nine regardless of where the player
    /// stands, which is what a clue about the far side of the map needs.</para>
    /// </summary>
    public static IReadOnlyList<Anchor> HousingShards()
    {
        if (_housingTerritory == Plugin.ClientState.TerritoryType && _housing != null) return _housing;

        var list = new List<Anchor>();

        try
        {
            uint territory = Plugin.ClientState.TerritoryType;
            var  sheet     = Plugin.DataManager.GetExcelSheet<LSheets.HousingAethernet>();

            if (sheet != null)
            {
                foreach (var h in sheet)
                {
                    if (h.TerritoryType.RowId != territory) continue;

                    string name = h.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                    if (name.Length == 0) continue;

                    if (h.Level.ValueNullable is not { } lvl) continue;
                    if (lvl.X == 0f && lvl.Y == 0f && lvl.Z == 0f) continue;

                    list.Add(new Anchor($"the {name} aethernet shard",
                                        new Vector3(lvl.X, lvl.Y, lvl.Z)));
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Clue] housing aethernet read failed: {ex.Message}");
        }

        _housingTerritory = Plugin.ClientState.TerritoryType;
        _housing          = list;
        return list;
    }

    private static uint          _housingTerritory = uint.MaxValue;
    private static List<Anchor>? _housing;

    /// <summary>
    /// Aethernet shards and aetherytes standing in the world near a position, named usefully.
    ///
    /// <para><b>The object's own name is generic</b> — every one of them is just "Aethernet Shard",
    /// which is a fine clue in a subdivision that has one and a useless one in a city that has eight.
    /// So it is qualified with the nearest named map SECTION when there is one close enough, giving
    /// "the Aethernet Shard in The Halberd's Head Subdivision" — which is how a person would say it.</para>
    /// </summary>
    private static IReadOnlyList<Anchor> LiveShards(Vector3 spot)
    {
        var list = new List<Anchor>();

        try
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj == null) continue;
                if (obj.ObjectKind != ObjectKind.EventObj) continue;

                string name = obj.Name.TextValue;
                if (name.Length == 0) continue;

                bool isShard = name.IndexOf("Aethernet", StringComparison.OrdinalIgnoreCase) >= 0
                            || name.IndexOf("Aetheryte", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isShard) continue;
                if (DigGround.Flat(obj.Position, spot) > ShardRadius) continue;

                list.Add(new Anchor(Qualify(name, obj.Position), obj.Position));
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Clue] shard scan failed: {ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// A shard can anchor a clue from further away than an NPC can, because unlike an NPC it does not
    /// wander off and is a genuine navigation landmark.
    /// </summary>
    private const float ShardRadius = 150f;

    /// <summary>Attaches the nearest named map section to a generic object name, when one is near.</summary>
    private static string Qualify(string name, Vector3 where)
    {
        string? best = null;
        float   bestD = 90f;

        foreach (var s in Sections())
        {
            float d = DigGround.Flat(s.World, where);
            if (d >= bestD) continue;

            bestD = d;
            best  = s.Name;
        }

        return best == null ? $"the {name}" : $"the {name} in {best}";
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
    public static IReadOnlyList<Anchor> NearbyNpcs(Vector3 spot)
    {
        // Whole-map placements first, then whatever is loaded but has no placement row. The live
        // table is the SUPPLEMENT now, not the source: it only ever holds what the client has
        // streamed in around the player, which in Empyreum meant one shard out of nine and a
        // handful of NPCs out of a zone full of them.
        var list = new List<Anchor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in MapNpcs())
        {
            if (DigGround.Flat(a.World, spot) > NpcRadius) continue;
            if (!seen.Add(a.Name)) continue;
            list.Add(a);
        }

        foreach (var a in NearbyOfKind(spot, ObjectKind.EventNpc))
        {
            if (!seen.Add(a.Name)) continue;
            list.Add(a);
        }

        return list;
    }

    /// <summary>
    /// How far from a spot a placed NPC may be and still anchor a clue. Wider than the live radius
    /// because a placement is a fixed, findable thing rather than something that may have wandered.
    /// </summary>
    private const float NpcRadius = 120f;

    /// <summary>
    /// Every named NPC PLACED in the current territory, from the <c>Level</c> sheet — the whole map,
    /// not merely what the client has loaded.
    ///
    /// <para><b>The discriminator is <c>Level.Object.RowId >= 1000000</c>, and it is not a guess.</b>
    /// It is lifted verbatim from this repo's own dataset generator
    /// (<c>scripts/gen-datasets/Program.cs</c>), where it produces all 30,878 entries of
    /// <c>data/npcs.json</c>. That threshold is what identifies an <c>ENpcResident</c> placement, and
    /// it is why no <c>Level.Type</c> enum value has to be assumed here — an earlier attempt stalled
    /// on exactly that, because guessing an enum is the kind of thing that compiles and is
    /// silently wrong.</para>
    ///
    /// <para><b>Read live rather than from npcs.json.</b> The dataset is the same data and is 14 MB;
    /// parsing it at runtime would cost a visible hitch and ship a file that can go stale against the
    /// game. The sheet is already in memory and is current by construction. The dataset's value is
    /// that it PROVED this path works, which is a different job from being the path.</para>
    ///
    /// <para><b>Enemies get no equivalent and cannot.</b> Monster spawns are not <c>Level</c> rows —
    /// <c>monsters.json</c> ships with <c>mapLocation: ???</c> for every one of its 14,560 entries
    /// because spawn tables are server-side. So the Enemy category stays live-only, and abstains for
    /// a spot the client has not streamed in. That is a real limit, not a gap to fill later.</para>
    /// </summary>
    public static IReadOnlyList<Anchor> MapNpcs()
    {
        if (_npcTerritory == Plugin.ClientState.TerritoryType && _npcs != null) return _npcs;

        var list = new List<Anchor>();

        try
        {
            uint territory = Plugin.ClientState.TerritoryType;

            var levels = Plugin.DataManager.GetExcelSheet<LSheets.Level>();
            var npcs   = Plugin.DataManager.GetExcelSheet<LSheets.ENpcResident>();

            if (levels != null && npcs != null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var l in levels)
                {
                    if (l.Territory.RowId != territory) continue;
                    if (l.Object.RowId < 1000000) continue;

                    string name = npcs.GetRowOrDefault(l.Object.RowId)?.Singular.ExtractText()
                                  ?? string.Empty;

                    if (IsBannedAnchor(name) || !seen.Add(name)) continue;

                    list.Add(new Anchor(name, new Vector3(l.X, l.Y, l.Z)));
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Clue] npc placement read failed: {ex.Message}");
        }

        _npcTerritory = Plugin.ClientState.TerritoryType;
        _npcs         = list;
        return list;
    }

    private static uint          _npcTerritory = uint.MaxValue;
    private static List<Anchor>? _npcs;

    /// <summary>
    /// Named hostile creatures the client currently has loaded near a position. Same streaming
    /// limitation as <see cref="NearbyNpcs"/>.
    ///
    /// <para><b>A mob clue names a KIND of creature, not an individual</b>, and the phrasing in the
    /// clue writer reflects that — the specific wharf rat standing there when the trail was generated
    /// will have wandered off or been killed by the time the player arrives, but wharf rats in
    /// general will still be roughly where wharf rats live.</para>
    /// </summary>
    public static IReadOnlyList<Anchor> NearbyEnemies(Vector3 spot) => EnemyGroups(spot);

    /// <summary>
    /// Hostile creatures near a position, collapsed into ONE anchor per species at the CENTRE of
    /// where that species is standing.
    ///
    /// <para><b>A group location is the right granularity, and an individual is the wrong one.</b>
    /// Sansflaire's call: "near the Specific-Named Wolf enemies" is a fine clue. The particular wolf that
    /// happened to be there when the trail was generated will have wandered off or been killed by
    /// the time the player arrives — but wolves in general will still be roughly where wolves live,
    /// so the centroid of the pack is both more stable and more useful than any one of them.</para>
    ///
    /// <para>It also means mob spawn POINTS are not needed for this category at all, which is the
    /// question the LGB probe was asked to settle. An area is enough.</para>
    /// </summary>
    private static IReadOnlyList<Anchor> EnemyGroups(Vector3 spot)
    {
        var sums   = new Dictionary<string, (Vector3 Sum, int Count)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj == null || obj.ObjectKind != ObjectKind.BattleNpc) continue;

                string name = obj.Name.TextValue;

                // A Striking Dummy is a BattleNpc and is housing furniture, which is precisely the
                // case this list exists for.
                if (IsBannedAnchor(name)) continue;
                if (DigGround.Flat(obj.Position, spot) > NearRadius) continue;

                var prev = sums.TryGetValue(name, out var p) ? p : (Vector3.Zero, 0);
                sums[name] = (prev.Item1 + obj.Position, prev.Item2 + 1);
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Clue] enemy scan failed: {ex.Message}");
        }

        var list = new List<Anchor>();

        foreach (var kv in sums)
        {
            var centre = kv.Value.Sum / kv.Value.Count;

            // Plural only when there genuinely is a group. "near the Ixali Windtalkers" reads as a
            // place; "near the Ixali Windtalkers" for one lone mob reads as a lie.
            list.Add(new Anchor(kv.Value.Count > 1 ? $"the {kv.Key}s" : $"the {kv.Key}", centre));
        }

        return list;
    }

    /// <summary>How far from a spot a loaded object may be and still be called "there".</summary>
    private const float NearRadius = 55f;

    /// <summary>
    /// Whether a name is banned as a clue anchor.
    ///
    /// <para><b>A clue anchor must be a place somebody could find on a map.</b> A Striking Dummy is
    /// housing furniture — wherever a player put it, gone when they remove it — so "north-east of
    /// the Striking Dummy" is unfollowable by anyone who has not already seen it. The name is real
    /// game data and the object is genuinely standing there, which is exactly why no automatic rule
    /// catches it: nothing in the sheets distinguishes a landmark from a furnishing.</para>
    /// </summary>
    public static bool IsBannedAnchor(string name)
    {
        if (name.Length == 0) return true;

        string bans = DigTuning.AnchorBans;
        if (string.IsNullOrWhiteSpace(bans)) return false;

        foreach (var raw in bans.Split(','))
        {
            var term = raw.Trim();
            if (term.Length == 0) continue;
            if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }

        return false;
    }

    /// <summary>
    /// Object kinds that can never anchor a clue, whatever they are called.
    ///
    /// <para><b>Retainers are excluded STRUCTURALLY and not by name, because their names are chosen
    /// by players.</b> No ban list could ever enumerate them, and one that tried would be banning
    /// arbitrary words. The kind is the durable fact: a retainer is somebody's summoned NPC standing
    /// wherever they left it, which disqualifies it regardless of what it is called. Same reasoning
    /// for companions, mounts and housing objects.</para>
    /// </summary>
    private static bool IsBannedKind(ObjectKind kind) => kind
        is ObjectKind.Retainer
        or ObjectKind.Companion
        or ObjectKind.Ornament;

    private static IReadOnlyList<Anchor> NearbyOfKind(Vector3 spot, ObjectKind kind)
    {
        var list = new List<Anchor>();

        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj == null || obj.ObjectKind != kind) continue;
                if (IsBannedKind(obj.ObjectKind)) continue;

                string name = obj.Name.TextValue;
                if (IsBannedAnchor(name)) continue;

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

    /// <summary>
    /// Counts what the current zone's LGB layer files actually contain, so the question "can we get
    /// mob spawn areas from client data" is answered by measurement instead of by argument.
    ///
    /// <para><b>This is a probe, not a source.</b> Mob spawn TABLES are server-side — settled as Q11,
    /// and why <c>monsters.json</c> ships <c>mapLocation: ???</c> for all 14,560 entries. But spawn
    /// POINTS are a different thing from spawn tables: the level geometry files carry placement
    /// objects, and Lumina can parse them (<c>LayerEntryType.BattleNPC</c> and
    /// <c>PopRangeInstanceObject</c> both exist). Whether retail LGB actually holds usable BattleNPC
    /// entries for overworld zones, with ids that resolve to names, is <b>unverified</b> — so this
    /// counts them in one click rather than committing to a parser that might find nothing.</para>
    ///
    /// <para>If the counts come back healthy across a few zones, an Enemy source covering the whole
    /// map is worth building. If they come back at zero, the Enemy category stays live-only and that
    /// is the end of it — which is a result, not a failure.</para>
    /// </summary>
    public static string ProbeZoneLayers()
    {
        try
        {
            var territories = Plugin.DataManager.GetExcelSheet<LSheets.TerritoryType>();

            if (territories?.GetRowOrDefault(Plugin.ClientState.TerritoryType) is not { } t)
                return "no TerritoryType row for this zone.";

            string bg = t.Bg.ExtractText();
            if (bg.Length == 0) return "this territory has no Bg path.";

            var lines = new List<string> { $"Bg = {bg}" };

            // The four layer files a territory normally carries. Named individually because which
            // one holds placements varies by zone age, and "not found" for one is not an error.
            foreach (var which in new[] { "planevent", "planlive", "planmap", "bg" })
            {
                string path = $"bg/{bg}/level/{which}.lgb";

                var file = Plugin.DataManager.GetFile<Lumina.Data.Files.LgbFile>(path);
                if (file == null) { lines.Add($"  {which}.lgb: absent"); continue; }

                int battle = 0, evt = 0, pop = 0, total = 0;

                foreach (var layer in file.Layers)
                {
                    foreach (var obj in layer.InstanceObjects)
                    {
                        total++;

                        switch (obj.AssetType)
                        {
                            case Lumina.Data.Parsing.Layer.LayerEntryType.BattleNPC: battle++; break;
                            case Lumina.Data.Parsing.Layer.LayerEntryType.EventNPC:  evt++;    break;
                            case Lumina.Data.Parsing.Layer.LayerEntryType.PopRange:  pop++;    break;
                        }
                    }
                }

                lines.Add($"  {which}.lgb: {file.Layers.Length} layer(s), {total} object(s) — "
                        + $"BattleNPC {battle}, EventNPC {evt}, PopRange {pop}");
            }

            return string.Join("\n[Challenges] ", lines);
        }
        catch (Exception ex)
        {
            return $"LGB probe failed: {ex.Message}";
        }
    }

    /// <summary>Drops the per-territory caches, so a zone change re-reads rather than remembering.</summary>
    public static void Invalidate()
    {
        _aetheryteTerritory = uint.MaxValue;
        _aetherytes         = null;
        _aetheryteReport    = "not read yet.";
        _housingTerritory   = uint.MaxValue;
        _housing            = null;
        _npcTerritory       = uint.MaxValue;
        _npcs               = null;
    }

    /// <summary>
    /// What each category can actually offer on this map right now, for the lab. Purely diagnostic:
    /// "the clues are all quadrants" and "only the quadrant category had anything to say" look
    /// identical from the player's side, and this tells them apart in one click.
    /// </summary>
    public static string Census(Vector3 around)
    {
        // The raw object-table tally alongside the filtered one. A category reporting zero while the
        // game plainly has that thing standing there is our filter, not the world — and printing
        // only the filtered number is what made an Aethernet Shard three yalms away read as "this
        // zone has no aetherytes". Twice in one session, so the raw counts stay visible.
        int npcs = 0, enemies = 0, objs = 0, shardObjs = 0;

        try
        {
            foreach (var o in Plugin.ObjectTable)
            {
                if (o == null) continue;

                switch (o.ObjectKind)
                {
                    case ObjectKind.EventNpc:  npcs++;    break;
                    case ObjectKind.BattleNpc: enemies++; break;
                    case ObjectKind.EventObj:
                        objs++;
                        if (o.Name.TextValue.IndexOf("Aethe", StringComparison.OrdinalIgnoreCase) >= 0)
                            shardObjs++;
                        break;
                }
            }
        }
        catch (Exception ex) { Diag.Error($"[Clue] census failed: {ex.Message}"); }

        return $"USABLE ANCHORS — landmarks {Landmarks().Count}   "
             + $"aetherytes+shards {AetherytesNear(around).Count} "
             + $"(Aetheryte sheet {Aetherytes().Count}, HousingAethernet {HousingShards().Count})   "
             + $"sections {Sections().Count}   "
             + $"npcs {NearbyNpcs(around).Count} (placed in zone {MapNpcs().Count})   "
             + $"enemies {NearbyEnemies(around).Count}\n"
             + $"[Challenges] OBJECT TABLE RAW — EventNpc {npcs}   BattleNpc {enemies}   "
             + $"EventObj {objs} (aethe-named {shardObjs}). "
             + "A usable count far below its raw count means our own filter, not the game.";
    }
}
#endif
