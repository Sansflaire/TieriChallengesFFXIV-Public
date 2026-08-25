using System;
using System.Collections.Generic;
using System.Linq;

using Lumina.Excel.Sheets;

namespace TieriChallengesFFXIV;

/// <summary>
/// Every zone in the game, grouped by expansion — the data behind the Zones grouping mode.
///
/// <para><b>Where this comes from.</b> It is read straight out of the game's own Excel sheets at
/// runtime, which is exactly what the in-game Teleport menu is built from: every
/// <see cref="Aetheryte"/> row points at the <see cref="TerritoryType"/> it sits in, and each
/// territory carries an <c>ExVersion</c> saying which expansion it shipped with. Walking the
/// aetheryte list therefore reproduces the teleport destination list, and grouping by ExVersion
/// reproduces the expansion filter along the top of that window.</para>
///
/// <para>This is deliberately NOT a transcribed table of zone names. A hand-written list would be
/// wrong the day a patch adds a zone, would be English-only, and would have to be maintained
/// forever. The sheets are already correct, already localised, and update themselves when the
/// game patches. <c>TerritoryType.ExVersion</c> is verified in shipping sibling code — see
/// <c>ClaudeAccessXIV/src/GameStateCollector.cs</c>, which reads
/// <c>row.Value.ExVersion.RowId</c> and <c>.ExVersion.ValueNullable?.Name</c>.</para>
///
/// <para>Built once and cached. The only thing that invalidates it is the challenge set changing,
/// because zones referenced by a challenge are merged in even when no aetheryte points at them —
/// a challenge in a zone you cannot teleport to must still be reachable in the list.</para>
/// </summary>
internal static class ZoneIndex
{
    /// <summary>A challenge with no territory bound to it. Not a real territory id.</summary>
    public const uint AnyZone = 0;

    /// <summary>Sorts after every real expansion, so the catch-all group sits at the bottom.</summary>
    private const uint AnyZoneExpansion = uint.MaxValue;

    /// <summary>
    /// Residential zones get their own group rather than being scattered through the expansion
    /// that happened to introduce them — Trist's call, and the in-game Teleport menu agrees: it
    /// gives housing its own filter tab, not a slot under A Realm Reborn.
    /// </summary>
    private const uint ResidentialExpansion = uint.MaxValue - 1;

    /// <summary>
    /// <c>TerritoryIntendedUse</c> values that mean "housing". 13 is the outdoor ward, 14 the
    /// interior. Verified against shipping sibling code — <c>ClaudeAccessXIV</c>'s
    /// <c>GameStateCollector</c> uses exactly this pair for its <c>isHousing</c> flag.
    /// </summary>
    private static bool IsResidential(uint intendedUse) => intendedUse is 13 or 14;

    public sealed record Zone(uint TerritoryId, string Name);

    public sealed record Expansion(uint Id, string Name, IReadOnlyList<Zone> Zones);

#if DEV_BUILD
    /// <summary>
    /// Sentinel base for the dev-only duty groups (Dungeons, Trials, Raids, …). Offset well clear
    /// of both real <c>ExVersion</c> ids (small — 0 through roughly 6 today) and the two other
    /// sentinels above, so an <see cref="Expansion.Id"/> can never collide between the three
    /// origins. <see cref="ResidentialExpansion"/> sits at <c>uint.MaxValue - 1</c>; this range
    /// tops out far below that.
    /// </summary>
    private const uint DutyGroupBase = uint.MaxValue - 1_000_000;

    /// <summary>
    /// Same ordering ClaudeAccessXIV's <c>ContentCatalogService.ContentTypeOrder</c> uses — the
    /// content types people actually track (Dungeons, Trials, Raids…) first, everything else
    /// alphabetical behind them. Duplicated rather than shared because that class lives in a
    /// different plugin and this one must not take a direct assembly reference to it.
    /// </summary>
    private static int DutyTypeRank(string contentType) => contentType switch
    {
        "Dungeons"      => 0,
        "Trials"        => 1,
        "Raids"         => 2,
        "Guildhests"    => 3,
        "PvP"           => 4,
        "Deep Dungeons" => 5,
        _               => 50,
    };
#endif

    /// <summary>
    /// Fallback expansion names. The ExVersion sheet supplies these, but row 0 has been known to
    /// carry an empty string, and a blank group header is worse than a hardcoded one. Only used
    /// when the sheet gives us nothing.
    /// </summary>
    private static readonly Dictionary<uint, string> FallbackNames = new()
    {
        [0] = "A Realm Reborn",
        [1] = "Heavensward",
        [2] = "Stormblood",
        [3] = "Shadowbringers",
        [4] = "Endwalker",
        [5] = "Dawntrail",
    };

    private static List<Expansion>? _cache;
    private static int  _cacheKey = -1;
    private static bool _sheetsFailed;

    /// <summary>territory id → zone name, for everything the index knows about.</summary>
    private static readonly Dictionary<uint, string> Names = new();

    /// <summary>Drop the cache. Called when the challenge set changes.</summary>
    public static void Invalidate()
    {
        _cacheKey = -1;
#if DEV_BUILD
        _devCacheKey = -1;
#endif
    }

#if DEV_BUILD
    private static List<Expansion>? _devCache;
    private static int  _devCacheKey = -1;

    /// <summary>
    /// DEVELOPER BUILDS ONLY. Every zone AND every duty (dungeon/trial/raid/…) in the game, not
    /// just the ones an aetheryte or an authored challenge already reaches — Trist's own
    /// "where do I still need to write a challenge" census, greyed by <see cref="Tally"/> exactly
    /// like the normal Zone tab already dims a populated-vs-empty zone.
    ///
    /// <para>Built from the same two sheets — and the same verified field names — as
    /// ClaudeAccessXIV's <c>ContentCatalogService</c>, the shipping sibling plugin whose
    /// <c>/game/zones</c> and <c>/game/content</c> endpoints this mirrors: every named,
    /// non-stub <see cref="TerritoryType"/> for the zone half, and every
    /// <see cref="ContentFinderCondition"/> for the duty half. That plugin's grounding notes
    /// record how <c>TerritoryType.ContentFinderCondition != 0</c> was confirmed to be exactly
    /// the zone/duty split, and how stub rows are recognised by an empty <c>Bg</c> path — this
    /// method makes the same two calls rather than re-deriving them.</para>
    ///
    /// <para><b>Simplification versus that service, made for effort reasons and worth knowing
    /// about:</b> duties are grouped by content type ONLY (Dungeons, Trials, Raids, …), not by
    /// content type THEN expansion — this plugin's zone tree is two levels (group → entry), not
    /// three, and adding a third would need a wider rewrite of <c>MainWindow.BuildZoneList</c>.
    /// Say so if the flattened Dungeons bucket is annoying in practice; a three-level tree is a
    /// real option later, not a wall.</para>
    ///
    /// <para>Also unlike the sibling service, this does not dedup by (PlaceName, intended use) —
    /// a seasonal or instanced variant of a zone can appear as two rows with the same display
    /// name. Harmless for a developer census; would be worth fixing for a player-facing list.</para>
    /// </summary>
    public static IReadOnlyList<Expansion> AllGameContent(Configuration cfg)
    {
        if (_devCache != null && _devCacheKey == cfg.StateVersion) return _devCache;

        var byGroup = new Dictionary<uint, Dictionary<uint, string>>();
        var expansionNames = new Dictionary<uint, string>();

        void Add(uint group, uint territory, string name)
        {
            if (territory == 0 || string.IsNullOrWhiteSpace(name)) return;
            if (!byGroup.TryGetValue(group, out var zones)) byGroup[group] = zones = new Dictionary<uint, string>();
            zones[territory] = name;
            Names[territory] = name;
        }

        try
        {
            var territories = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (territories != null)
            {
                foreach (var t in territories)
                {
                    string name = t.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Stub / unreleased rows carry no background path — same filter
                    // ContentCatalogService uses to keep junk rows out of its own zone list.
                    if (t.Bg.ToString().Length == 0) continue;

                    // A territory a ContentFinderCondition points at IS a duty; it is added below
                    // from the CFC walk instead, so it is not double-counted as a "zone" here.
                    if (t.ContentFinderCondition.RowId != 0) continue;

                    uint ex = t.ExVersion.RowId;
                    string exName = t.ExVersion.ValueNullable?.Name.ToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(exName)) expansionNames[ex] = exName;

                    if (IsResidential(t.TerritoryIntendedUse.RowId)) ex = ResidentialExpansion;

                    Add(ex, t.RowId, name);
                }
            }

            var cfcSheet = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
            if (cfcSheet != null)
            {
                foreach (var c in cfcSheet)
                {
                    string name = c.Name.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (c.TerritoryType.RowId == 0) continue;

                    // ContentFinderCondition.Name is stored lower-cased ("the navel"); the game
                    // title-cases it at display time. Same fix ContentCatalogService applies.
                    if (char.IsLower(name[0])) name = char.ToUpperInvariant(name[0]) + name.Substring(1);

                    string typeName = c.ContentType.ValueNullable?.Name.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(typeName)) typeName = "Other";

                    uint group = DutyGroupBase + (uint)DutyTypeRank(typeName) * 1000u + c.ContentType.RowId;
                    expansionNames[group] = typeName;

                    Add(group, c.TerritoryType.RowId, name);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Zones] could not build the developer zone/duty census");
        }

        var result = new List<Expansion>(byGroup.Count);
        foreach (var kv in byGroup)
        {
            var zones = new List<Zone>(kv.Value.Count);
            foreach (var z in kv.Value) zones.Add(new Zone(z.Key, z.Value));
            zones.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            result.Add(new Expansion(kv.Key, ExpansionName(kv.Key, expansionNames), zones));
        }
        result.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        Plugin.Log.Information($"[Zones] developer census: {result.Sum(e => e.Zones.Count)} entr(ies) "
                              + $"in {result.Count} group(s)");

        _devCache    = result;
        _devCacheKey = cfg.StateVersion;
        return result;
    }
#endif

    /// <summary>
    /// The grouped zone list. Expansions in release order; zones alphabetical within each, which
    /// is how the request was framed and is the only ordering that makes a 150-row list findable.
    /// </summary>
    public static IReadOnlyList<Expansion> Expansions(Configuration cfg)
    {
        if (_cache != null && _cacheKey == cfg.StateVersion) return _cache;

        var byExpansion = new Dictionary<uint, Dictionary<uint, string>>();
        var expansionNames = new Dictionary<uint, string>();

        void Add(uint expansion, uint territory, string name)
        {
            if (territory == 0 || string.IsNullOrWhiteSpace(name)) return;

            if (!byExpansion.TryGetValue(expansion, out var zones))
                byExpansion[expansion] = zones = new Dictionary<uint, string>();

            zones[territory] = name;
            Names[territory] = name;
        }

        try
        {
            var territories = Plugin.DataManager.GetExcelSheet<TerritoryType>();

            // Every aetheryte AND every aethernet shard is walked, not just the big teleport
            // targets. Shards are what reach the city sub-zones (Old Gridania, the various
            // Ul'dah wards) that have no teleport destination of their own, and skipping them
            // would quietly drop real zones from the list.
            var aetherytes = Plugin.DataManager.GetExcelSheet<Aetheryte>();
            if (aetherytes != null)
            {
                foreach (var row in aetherytes)
                {
                    var territory = row.Territory.ValueNullable;
                    if (territory == null) continue;

                    uint tid = territory.Value.RowId;
                    if (tid == 0) continue;

                    string name = territory.Value.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
                    uint   ex   = territory.Value.ExVersion.RowId;

                    string exName = territory.Value.ExVersion.ValueNullable?.Name.ToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(exName)) expansionNames[ex] = exName;

                    if (IsResidential(territory.Value.TerritoryIntendedUse.RowId))
                        ex = ResidentialExpansion;

                    Add(ex, tid, name);
                }
            }

            // Zones a challenge points at but no aetheryte does — a duty, an instance, anywhere
            // you cannot teleport. Merged in so every challenge is reachable through the list.
            foreach (var c in ChallengeCatalog.AllTrackable(cfg))
            {
                if (c.TerritoryId == 0) continue;
                if (Names.ContainsKey(c.TerritoryId)) continue;

                var row = territories?.GetRowOrDefault(c.TerritoryId);
                string name = row?.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;

                // Fall back to the name captured when the challenge was authored, then to the
                // raw id. A nameless row is still better than a challenge with nowhere to live.
                if (string.IsNullOrWhiteSpace(name)) name = c.TerritoryName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name)) name = $"Territory {c.TerritoryId}";

                uint ex = row != null && IsResidential(row.Value.TerritoryIntendedUse.RowId)
                    ? ResidentialExpansion
                    : row?.ExVersion.RowId ?? 0;

                Add(ex, c.TerritoryId, name);
            }
        }
        catch (Exception ex)
        {
            // A missing sheet must not take the window down — the grouping toggle simply has
            // nothing to show, and Categories mode is unaffected.
            if (!_sheetsFailed)
            {
                _sheetsFailed = true;
                Plugin.Log.Error(ex, "[Zones] could not build the zone index from the game sheets");
            }
        }

        // The catch-all bucket exists only when something actually lives in it, so a normal
        // catalogue never shows an empty "Anywhere" group.
        foreach (var c in ChallengeCatalog.AllTrackable(cfg))
        {
            if (c.TerritoryId != 0) continue;
            byExpansion.TryAdd(AnyZoneExpansion, new Dictionary<uint, string>());
            break;
        }

        var result = new List<Expansion>(byExpansion.Count);
        foreach (var kv in byExpansion)
        {
            var zones = new List<Zone>(kv.Value.Count);
            foreach (var z in kv.Value) zones.Add(new Zone(z.Key, z.Value));

            zones.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            result.Add(new Expansion(kv.Key, ExpansionName(kv.Key, expansionNames), zones));
        }

        result.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        // Logged on every rebuild (rare — only when the challenge set changes). This is the only
        // way to confirm the sheet walk actually found the zones rather than silently producing a
        // short list, and it is what proved the expansion split matches the Teleport window.
        if (result.Count > 0)
        {
            int total = 0;
            var summary = new List<string>(result.Count);
            foreach (var e in result)
            {
                total += e.Zones.Count;
                summary.Add($"{e.Name} {e.Zones.Count}");
            }
            Plugin.Log.Information($"[Zones] indexed {total} zone(s) in {result.Count} group(s): {string.Join(", ", summary)}");
        }

        _cache    = result;
        _cacheKey = cfg.StateVersion;
        return result;
    }

    private static string ExpansionName(uint id, Dictionary<uint, string> fromSheet)
    {
        if (id == AnyZoneExpansion)     return "Not tied to a zone";
        if (id == ResidentialExpansion) return "Residential Areas";
        if (fromSheet.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)) return name;
        if (FallbackNames.TryGetValue(id, out var known)) return known;
        return $"Expansion {id}";
    }

    /// <summary>Display name for a territory, or a readable placeholder when it is unknown.</summary>
    public static string ZoneName(uint territoryId)
    {
        if (territoryId == AnyZone) return "Not tied to a zone";
        return Names.TryGetValue(territoryId, out var name) ? name : $"Territory {territoryId}";
    }

    /// <summary>
    /// The name to show a PLAYER — masked when <see cref="AttunementService.IsZoneSpoilered"/>
    /// says so. Every player-facing surface that would otherwise print a real zone name (tooltips,
    /// right-click-teleport error text, the zone list itself) must go through this, not
    /// <see cref="ZoneName"/> directly, or the mask in the list is trivially defeated by hovering
    /// or by triggering an error message.
    /// </summary>
    public static string DisplayName(Configuration cfg, uint territoryId) =>
        AttunementService.IsZoneSpoilered(cfg, territoryId) ? "??? (unexplored)" : ZoneName(territoryId);

    /// <summary>
    /// Per-zone x-of-y, tallied in ONE pass.
    ///
    /// <para>This type exists for a performance reason, not a tidiness one. The obvious shape —
    /// a <c>ZoneProgress(zone)</c> helper called from each row — walks the whole catalogue per
    /// row, and the zone list is ~150 rows across six expansion headers. That is two orders of
    /// magnitude more work per frame than the category view, on a plugin whose stated goal is to
    /// be cheap. Tally once, read many.</para>
    /// </summary>
    public sealed class Counts
    {
        private readonly Dictionary<uint, (int Done, int Total)> _byZone = new();

        public (int Done, int Total) Zone(uint territoryId) =>
            _byZone.TryGetValue(territoryId, out var v) ? v : (0, 0);

        public (int Done, int Total) Of(Expansion expansion)
        {
            if (expansion.Id == AnyZoneExpansion) return Zone(AnyZone);

            int done = 0, total = 0;
            foreach (var zone in expansion.Zones)
            {
                var (d, t) = Zone(zone.TerritoryId);
                done  += d;
                total += t;
            }
            return (done, total);
        }

        internal void Record(uint territoryId, bool complete)
        {
            _byZone.TryGetValue(territoryId, out var v);
            _byZone[territoryId] = (v.Done + (complete ? 1 : 0), v.Total + 1);
        }
    }

    /// <summary>One pass over the catalogue, producing every zone's counts.</summary>
    public static Counts Tally(Configuration cfg, CompletionStore store)
    {
        var counts = new Counts();

        // Resolve territory by walking the definitions once and indexing them, rather than
        // calling FindCustom per challenge — that is a linear search inside a linear loop.
        var territoryById = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ChallengeCatalog.AllTrackable(cfg))
            if (!string.IsNullOrWhiteSpace(c.Id)) territoryById[c.Id] = c.TerritoryId;

        foreach (var def in ChallengeCatalog.Combined(cfg))
        {
            territoryById.TryGetValue(def.Id, out uint tid);
            counts.Record(tid, store.IsComplete(def.Id));
        }

        return counts;
    }

    /// <summary>Challenges bound to one zone, in the same sort order the category view uses.</summary>
    public static List<ChallengeDef> InZone(Configuration cfg, uint territoryId)
    {
        var list = new List<ChallengeDef>();
        foreach (var def in ChallengeCatalog.Combined(cfg))
            if (TerritoryOf(cfg, def.Id) == territoryId) list.Add(def);
        return list;
    }

    /// <summary>Which zone a challenge belongs to. 0 when it is not bound to one.</summary>
    /// <remarks>
    /// A quest chain reports its CURRENT step's zone, so it re-files itself under wherever it
    /// currently points as the player works through it — see
    /// <see cref="ChallengeCatalog.EffectiveTerritory"/>.
    /// </remarks>
    public static uint TerritoryOf(Configuration cfg, string challengeId)
    {
        var c = ChallengeCatalog.FindCustom(cfg, challengeId);
        return c == null ? 0u : ChallengeCatalog.EffectiveTerritory(c);
    }

    /// <summary>
    /// Select a zone and make sure it is actually visible: a revealed challenge inside a
    /// collapsed expansion would otherwise select a row nobody can see.
    /// </summary>
    public static void Reveal(Configuration cfg, uint territoryId)
    {
        cfg.SelectedTerritory = (int)territoryId;

        foreach (var expansion in Expansions(cfg))
        {
            bool holdsIt = expansion.Id == AnyZoneExpansion
                ? territoryId == AnyZone
                : expansion.Zones.Any(z => z.TerritoryId == territoryId);

            if (holdsIt)
            {
                cfg.CollapsedExpansions.Remove(expansion.Id);
                return;
            }
        }
    }
}
