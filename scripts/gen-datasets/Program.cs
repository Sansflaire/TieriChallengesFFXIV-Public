using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lumina;
using Lumina.Excel.Sheets;

const string U = "???";
var OUT = @"C:\Users\trist\AppData\Roaming\XIVLauncher\devPlugins\TieriChallengesFFXIV\data";
Directory.CreateDirectory(OUT);

var gd = new GameData(@"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack");
var JO = new JsonSerializerOptions { WriteIndented = false };

string T(object? o) => o?.ToString() ?? "";

var items = gd.GetExcelSheet<Item>();
var terr  = gd.GetExcelSheet<TerritoryType>();
var pn    = gd.GetExcelSheet<PlaceName>();
var quest = gd.GetExcelSheet<Quest>();
var cjc   = gd.GetExcelSheet<ClassJobCategory>();
var exv   = gd.GetExcelSheet<ExVersion>();

string IName(uint id) => items.GetRowOrDefault(id) is { } i ? T(i.Name) : U;
string Zone(uint tid) => terr.GetRowOrDefault(tid) is { } t
    ? (pn.GetRowOrDefault(t.PlaceName.RowId) is { } p ? T(p.Name) : U) : U;

/// <summary>
/// Serialises a dataset, shrinking it three ways without losing a single fact:
///
/// 1. A field that is "???" on EVERY entry is stripped from the entries and named once in
///    <c>omittedAlwaysUnknown</c>. Storing the same "???" 30,000 times carries no information the
///    header does not already carry. A field that is "???" only SOMETIMES keeps its literal
///    "???", because there the per-row value is real information.
/// 2. Keys are aliased to short tokens with a <c>fieldAliases</c> legend. Long descriptive names
///    are worth having exactly once, not once per row.
/// 3. No indentation. These are machine-generated and read through the in-game viewer.
///
/// The viewer reverses all three, so what a human sees is unchanged — "???" included.
/// </summary>
void Write(string file, string desc, string? needs, string[] unknown, object entries, int count,
           Dictionary<string, string[]>? groups = null, string curatedKey = "id")
{
    var path = Path.Combine(OUT, file);

    var arr = JsonSerializer.SerializeToNode(entries) as JsonArray ?? new JsonArray();

    // ---- curated overlay -------------------------------------------------------------
    // Data that CANNOT come from game files (external sources, hand research) lives in
    // data/curated/<file> and is folded in HERE, during generation. That is the whole point:
    // regenerating a dataset used to silently destroy it, because the merge happened afterwards
    // and nothing re-ran it. Now the overlay is an input to generation, so regeneration is
    // idempotent and lossless no matter how often it runs.
    //
    // The generator NEVER writes to data/curated. It is hand-owned and read-only from here.
    // A dataset may have SEVERAL overlays, one per external source:
    //
    //     curated/duties.json         <- Garland Tools sweep  (unlockQuest, itemsFound, ...)
    //     curated/duties.wiki.json    <- Final Fantasy Wiki   (monsters)
    //
    // They are separate files on purpose. One file per source means two independent research
    // pipelines can each re-run without either destroying the other's work - which is the same
    // failure that made curated data an input to generation in the first place (TODO A10).
    // Applied in a fixed order: the bare name first, then the suffixed ones alphabetically, so
    // the result never depends on directory enumeration order.
    var curatedFields = new List<string>();
    var curatedSources = new List<string>();
    int curatedApplied = 0;
    var curDir = Path.Combine(OUT, "curated");
    var stem = Path.GetFileNameWithoutExtension(file);

    var overlayPaths = new List<string>();
    if (Directory.Exists(curDir))
    {
        var exact = Path.Combine(curDir, file);
        if (File.Exists(exact)) overlayPaths.Add(exact);
        foreach (var f in Directory.GetFiles(curDir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            var n = Path.GetFileName(f);
            if (n.StartsWith(stem + ".", StringComparison.Ordinal) && n != file)
                overlayPaths.Add(f);
        }
    }

    foreach (var curatedPath in overlayPaths)
    {
        var shortName = Path.GetFileName(curatedPath);
        try
        {
            var cur = JsonNode.Parse(File.ReadAllText(curatedPath)) as JsonObject;
            var src = cur?["source"]?.GetValue<string>() ?? "";
            var key = cur?["keyField"]?.GetValue<string>() ?? curatedKey;
            var byKey = cur?["entries"] as JsonObject;
            int applied = 0;

            if (byKey is not null)
            {
                foreach (var n in arr)
                {
                    if (n is not JsonObject o) continue;
                    if (!o.TryGetPropertyValue(key, out var kv) || kv is null) continue;
                    var patch = byKey[kv.ToJsonString().Trim('"')] as JsonObject;
                    if (patch is null) continue;

                    foreach (var kvp in patch)
                    {
                        o[kvp.Key] = kvp.Value?.DeepClone();
                        if (!curatedFields.Contains(kvp.Key)) curatedFields.Add(kvp.Key);
                    }
                    applied++;
                }
            }
            curatedApplied += applied;
            if (src.Length > 0) curatedSources.Add(src);
            Console.WriteLine($"    curated: {applied} entries patched from curated/{shortName}");
        }
        catch (Exception ex)
        {
            // Loud, not silent. A malformed overlay must never look like "there was no overlay".
            Console.WriteLine($"    !! curated/{shortName} FAILED TO LOAD: {ex.Message}");
            Console.WriteLine($"    !! regenerated WITHOUT that curated data - do not commit this output");
        }
    }
    string curatedSource = string.Join(" | ", curatedSources);

    // Key order of first appearance, so aliases are stable and the viewer's columns keep the
    // order the generator wrote them in.
    var order = new List<string>();
    var seenKey = new HashSet<string>(StringComparer.Ordinal);
    var alwaysUnknown = new HashSet<string>(StringComparer.Ordinal);
    var everKnown = new HashSet<string>(StringComparer.Ordinal);

    foreach (var n in arr)
    {
        if (n is not JsonObject o) continue;
        foreach (var kv in o)
        {
            if (seenKey.Add(kv.Key)) order.Add(kv.Key);
            bool isUnknown = kv.Value is JsonValue v
                             && v.TryGetValue<string>(out var s) && s == U;
            if (isUnknown) alwaysUnknown.Add(kv.Key);
            else everKnown.Add(kv.Key);
        }
    }
    alwaysUnknown.ExceptWith(everKnown);   // "always" means never once known

    // Generalisation of the same idea: a field carrying the SAME value on every entry is stored
    // once in the header instead of N times. always-??? is just the case where that value is
    // "???". Costs nothing to restore and turns a repeated boilerplate note into one string.
    var constant = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
    if (arr.Count > 1)
    {
        foreach (var k in order)
        {
            if (alwaysUnknown.Contains(k)) continue;
            string? first = null;
            bool same = true, present = true;
            foreach (var n in arr)
            {
                if (n is not JsonObject o || !o.TryGetPropertyValue(k, out var v)) { present = false; break; }
                string txt = v?.ToJsonString() ?? "null";
                if (first is null) first = txt;
                else if (txt != first) { same = false; break; }
            }
            if (present && same && first is not null)
                constant[k] = JsonNode.Parse(first);
        }
    }

    var kept = order.Where(k => !alwaysUnknown.Contains(k) && !constant.ContainsKey(k)).ToList();
    var alias = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < kept.Count; i++) alias[kept[i]] = Alias(i);

    var slim = new JsonArray();
    foreach (var n in arr)
    {
        if (n is not JsonObject o) continue;
        var t = new JsonObject();
        foreach (var k in kept)
            if (o.TryGetPropertyValue(k, out var val))
                t[alias[k]] = val?.DeepClone();
        slim.Add(t);
    }

    var doc = new JsonObject
    {
        ["schemaVersion"] = 2,
        ["generated"] = DateTime.UtcNow.ToString("o"),
        ["source"] = "FFXIV sqpack via Lumina. Regenerate: see data/README.md",
        ["description"] = desc,
        ["needsVerification"] = needs,
        ["unknownFields"] = new JsonArray(unknown.Select(x => (JsonNode?)x).ToArray()),
        ["omittedAlwaysUnknown"] = new JsonArray(
            alwaysUnknown.OrderBy(x => order.IndexOf(x)).Select(x => (JsonNode?)x).ToArray()),
        ["unknownMarker"] = U,
        ["omittedConstant"] = new JsonObject(
            constant.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value?.DeepClone()))),
        ["columnGroups"] = groups is null
            ? new JsonObject()
            : new JsonObject(groups.Select(kv => new KeyValuePair<string, JsonNode?>(
                  kv.Key, new JsonArray(kv.Value.Select(x => (JsonNode?)x).ToArray())))),
        ["fieldAliases"] = new JsonObject(alias.Select(kv =>
            new KeyValuePair<string, JsonNode?>(kv.Value, kv.Key))),
        // Provenance: which columns are NOT from game files. The viewer surfaces this so curated
        // data is never mistaken for extracted data when reviewing.
        ["curatedFields"] = new JsonArray(curatedFields.Select(x => (JsonNode?)x).ToArray()),
        ["curatedSource"] = curatedSource,
        ["curatedEntryCount"] = curatedApplied,
        ["count"] = count,
        ["entries"] = slim,
    };

    File.WriteAllText(path, doc.ToJsonString(JO));
    Console.WriteLine($"  {file,-30} {count,6} entries  {new FileInfo(path).Length / 1024,7} KB"
                    + $"  (-{alwaysUnknown.Count} ???, -{constant.Count} constant)");
}

/// <summary>a…z, then aa, ab… Short, stable, and legible in a diff.</summary>
static string Alias(int i)
{
    var sb = new StringBuilder();
    do { sb.Insert(0, (char)('a' + i % 26)); i = i / 26 - 1; } while (i >= 0);
    return sb.ToString();
}

Console.WriteLine("writing:");

// ---------------- 1. DUTIES ----------------
var cfc = gd.GetExcelSheet<ContentFinderCondition>();
var ctype = gd.GetExcelSheet<ContentType>();
var cmt = gd.GetExcelSheet<ContentMemberType>();
var duties = new List<object>();
foreach (var c in cfc)
{
    if (T(c.Name).Length == 0) continue;
    uint ct = c.ContentType.RowId;
    if (ct != 2 && ct != 4 && ct != 5 && ct != 28 && ct != 37) continue;

    // Party size comes from ContentMemberType, NOT QueueMaxPlayers - that column is 0 for every
    // duty in the game and an earlier version of this file classified raids with it, which meant
    // the alliance/normal split was decided by a value that is always zero.
    var mt = cmt.GetRowOrDefault(c.ContentMemberType.RowId);
    int perParty = mt is { } m1 ? m1.MembersPerParty : 0;
    int parties  = mt is { } m2 ? Math.Max((int)m2.PartyCount, 1) : 1;
    int partySize = perParty * parties;

    string kind = ct == 2 ? "Dungeon" : ct == 4 ? "Trial" : ct == 28 ? "Ultimate"
                : ct == 37 ? "Chaotic Alliance Raid"
                : (partySize >= 24 ? "Alliance Raid" : "Normal Raid");

    duties.Add(new
    {
        id = c.RowId,
        name = T(c.Name),
        kind,
        contentType = T(ctype.GetRowOrDefault(ct)?.Name),
        territoryId = c.TerritoryType.RowId,
        zone = Zone(c.TerritoryType.RowId),
        expansion = T(exv.GetRowOrDefault(c.RequiredExVersion.RowId)?.Name),
        levelRequired = (int)c.ClassJobLevelRequired,
        levelSync = (int)c.ClassJobLevelSync,

        // BOTH item-level bounds. They are separate columns and a duty rarely has both: an
        // Ultimate carries only the sync ceiling, a Savage tier only the entry floor. Reading one
        // and calling it "the" item level is how a 0 gets mistaken for a missing value.
        itemLevelMin = (int)c.ItemLevelRequired,
        itemLevelMaxSync = (int)c.ItemLevelSync,

        partySize,
        partiesInAlliance = parties,
        highEndDuty = c.HighEndDuty,

        // ContentFinderCondition.Content IS Garland Tools' instance id - verified against
        // 30067 (Weapon's Refrain) and 30155 (AAC Heavyweight M1 Savage). Stored so any future
        // cross-check is a direct lookup rather than a name search.
        garlandId = c.Content.RowId,
        // ONE unlock column, defaulting to ??? and filled from the curated overlay.
        //
        // The game files simply do not carry this for duties: of 373 dungeons/trials/raids,
        // exactly ONE has any UnlockCriteria at all, and its UnlockType is 2 rather than 1, so it
        // does not even reference a quest. Four columns describing that absence - three of them
        // constant, one of them ??? on every single row - was noise pretending to be data.
        // Unlock is curated by necessity, and the header records that it is.
        unlockQuest = U,

        monsters = U,
        itemsFound = U
    });
}
Write("duties.json",
    "Dungeons, trials, normal/alliance raids, ultimates and chaotic alliance raids.",
    "PARTIAL - 'monsters' is CURATED from the Final Fantasy Wiki and covers 207 of these 373. The "
  + "166 without one are mostly Trials and Raids, whose bosses the wiki documents on pages outside "
  + "the enemy tables this was built from. 'unlockQuest' is CURATED: the game files carry unlock criteria for "
  + "exactly 1 of these 373 duties, so it is external research by necessity, not by preference. "
  + "A ??? there means we do not know - some Savage and Extreme tiers are unlocked by clearing "
  + "the normal version rather than by any quest, and that is indistinguishable from missing.",
    new[] { "monsters", "unlockQuest (where ???)" },
    duties, duties.Count);

Console.WriteLine($"    duties: {duties.Count} rows, "
                + $"{cfc.Count(c => T(c.Name).Length > 0 && (c.ContentType.RowId is 2 or 4 or 5 or 28 or 37) && c.ItemLevelRequired > 0)} with a min ilvl, "
                + $"{cfc.Count(c => T(c.Name).Length > 0 && (c.ContentType.RowId is 2 or 4 or 5 or 28 or 37) && c.ItemLevelSync > 0)} with a sync ceiling");

// ---------------- 2. MONSTERS ----------------
var bnpc = gd.GetExcelSheet<BNpcName>();
var mnt  = gd.GetExcelSheet<MonsterNoteTarget>();
var zonesOf = new Dictionary<uint, List<string>>();
foreach (var t in mnt)
{
    if (t.BNpcName.RowId == 0) continue;
    var zs = t.PlaceNameZone.Where(z => z.RowId != 0)
                            .Select(z => T(pn.GetRowOrDefault(z.RowId)?.Name))
                            .Where(s => s.Length > 0).ToList();
    if (zs.Count == 0) continue;
    if (!zonesOf.TryGetValue(t.BNpcName.RowId, out var l)) { l = new(); zonesOf[t.BNpcName.RowId] = l; }
    foreach (var z in zs) if (!l.Contains(z)) l.Add(z);
}
var mons = new List<object>();
foreach (var b in bnpc)
{
    string n = T(b.Singular);
    if (n.Length == 0) continue;
    bool hasZone = zonesOf.TryGetValue(b.RowId, out var z);
    mons.Add(new
    {
        id = b.RowId,
        name = n,
        zones = hasZone ? (object)z! : U,
        zonesSource = hasZone ? "Hunting Log (MonsterNoteTarget)" : U,
        mapLocation = U,
        level = U,
        drops = U,
        abilities = U,
        inInstance = U
    });
}
Write("monsters.json",
    "Every named battle NPC in the game.",
    "PARTIAL. The client supplies only id and name; zones for ~259 entries come from the Hunting "
  + "Log. EVERYTHING ELSE IS CURATED from the Final Fantasy Wiki (see curatedFields) and carries "
  + "that source's caveats - the wiki states plainly that its classifications are part conjecture, "
  + "and its levels/HP/abilities are editor-recorded rather than extracted. It documents the "
  + "notable ~3,500 of these 14,560, so most entries still have nothing. 'drops' stays ??? for "
  + "every entry: mob loot tables are server-side and absent from client data (Q11, settled).",
    new[] { "drops", "mapLocation", "inInstance", "level / zones / abilities (most entries)" },
    mons, mons.Count);

// ---------------- 3. LEVEL-BASED RECIPES ----------------
var rec = gd.GetExcelSheet<Recipe>();
var rlt = gd.GetExcelSheet<RecipeLevelTable>();
var ctr = gd.GetExcelSheet<CraftType>();
var recs = new List<Dictionary<string, object?>>();
var recipeIngredients = new List<List<string>>();
int maxIngredients = 0;
foreach (var r in rec)
{
    if (r.ItemResult.RowId == 0) continue;
    if (r.SecretRecipeBook.RowId != 0 || r.IsSpecializationRequired || r.IsExpert
        || r.Quest.RowId != 0 || r.StatusRequired.RowId != 0 || r.ItemRequired.RowId != 0) continue;

    var lt = rlt.GetRowOrDefault(r.RecipeLevelTable.RowId);
    var ing = new List<string>();
    for (int k = 0; k < r.Ingredient.Count; k++)
    {
        var g = r.Ingredient[k];
        byte a = r.AmountIngredient[k];
        if (g.RowId == 0 || a == 0) continue;
        ing.Add($"{a}x {IName(g.RowId)}");
    }
    if (ing.Count > maxIngredients) maxIngredients = ing.Count;
    recipeIngredients.Add(ing);

    recs.Add(new Dictionary<string, object?>
    {
        ["recipeId"] = r.RowId,
        ["craftType"] = T(ctr.GetRowOrDefault(r.CraftType.RowId)?.Name),
        ["resultItemId"] = r.ItemResult.RowId,
        ["resultName"] = IName(r.ItemResult.RowId),
        ["resultAmount"] = (int)r.AmountResult,
        ["recipeLevel"] = (int)(lt?.ClassJobLevel ?? 0),
        ["stars"] = (int)(lt?.Stars ?? 0),
        ["difficulty"] = (int)(lt?.Difficulty ?? 0),
        ["durability"] = (int)(lt?.Durability ?? 0),
        ["canHq"] = r.CanHq,
        ["canQuickSynth"] = r.CanQuickSynth,
        // Flat, one column each. A nested unlock object rendered as raw JSON in a grid cell and
        // was unreadable at a glance - which is the only thing a grid is for.
        ["unlockType"] = "level",
        ["unlockClassJobLevel"] = (int)(lt?.ClassJobLevel ?? 0),
        ["unlockBook"] = null,
        ["unlockNote"] = "no book, quest, status, held-item or specialist gate",
    });
}

// Ingredients become ingredient1..N so each sits in its own sortable, filterable column. The
// count is measured rather than assumed - the sheet's array is fixed-width and padded, so the
// real maximum is a property of the data, not of the schema.
for (int i = 0; i < recs.Count; i++)
    for (int k = 0; k < maxIngredients; k++)
        recs[i]["ingredient" + (k + 1)] = k < recipeIngredients[i].Count ? recipeIngredients[i][k] : null;

var recipeGroups = new Dictionary<string, string[]>
{
    ["ingredient (any)"] = Enumerable.Range(1, maxIngredients).Select(k => "ingredient" + k).ToArray(),
};
Write("recipes-level-based.json",
    "Plain level-based recipes ONLY. Master/Expert/Specialist/quest/status/item-gated recipes are "
  + "excluded by construction, so every entry here is safe for a generated quest.",
    null, Array.Empty<string>(), recs, recs.Count, recipeGroups);

// ---------------- 4. GATHERABLES + FISH ----------------
var gi   = gd.GetExcelSheet<GatheringItem>();
var conv = gd.GetExcelSheet<GatheringItemLevelConvertTable>();
var gpb  = gd.GetExcelSheet<GatheringPointBase>();
var gt   = gd.GetExcelSheet<GatheringType>();
var gp   = gd.GetExcelSheet<GatheringPoint>();

var baseZones = new Dictionary<uint, List<string>>();
foreach (var p in gp)
{
    uint bid = p.GatheringPointBase.RowId;
    if (bid == 0) continue;
    string z = T(pn.GetRowOrDefault(p.PlaceName.RowId)?.Name);
    if (z.Length == 0) z = Zone(p.TerritoryType.RowId);
    if (z.Length == 0 || z == U) continue;
    if (!baseZones.TryGetValue(bid, out var l)) { l = new(); baseZones[bid] = l; }
    if (!l.Contains(z)) l.Add(z);
}
var itemTypes = new Dictionary<uint, HashSet<string>>();
var itemZones = new Dictionary<uint, HashSet<string>>();
foreach (var b in gpb)
{
    string tn = T(gt.GetRowOrDefault(b.GatheringType.RowId)?.Name);
    foreach (var slot in b.Item)
    {
        uint giRow = (uint)slot.RowId;
        if (giRow == 0) continue;
        var g = gi.GetRowOrDefault(giRow);
        if (g is null) continue;
        uint it = (uint)g.Value.Item.RowId;
        if (it == 0 || it >= 1000000) continue;
        if (tn.Length > 0)
        {
            if (!itemTypes.TryGetValue(it, out var ts)) { ts = new(); itemTypes[it] = ts; }
            ts.Add(tn);
        }
        if (baseZones.TryGetValue(b.RowId, out var bz))
        {
            if (!itemZones.TryGetValue(it, out var zs)) { zs = new(); itemZones[it] = zs; }
            foreach (var z in bz) zs.Add(z);
        }
    }
}
var gathers = new List<object>();
foreach (var g in gi)
{
    uint it = (uint)g.Item.RowId;
    if (it == 0 || it >= 1000000) continue;
    var lvl = conv.GetRowOrDefault(g.GatheringItemLevel.RowId);
    gathers.Add(new
    {
        itemId = it,
        name = IName(it),
        discipline = "Miner/Botanist",
        gatheringType = itemTypes.TryGetValue(it, out var ts) ? (object)ts.ToArray() : U,
        levelRequired = (int)(lvl?.GatheringItemLevel ?? 0),
        stars = (int)(lvl?.Stars ?? 0),
        perceptionRequired = (int)g.PerceptionReq,
        isHidden = g.IsHidden,
        nodeLocations = itemZones.TryGetValue(it, out var zs) ? (object)zs.ToArray() : U,
        isCollectable = U,
        isTimedNode = U,
        isLegendaryNode = U
    });
}
var fp = gd.GetExcelSheet<FishParameter>();
var fs = gd.GetExcelSheet<FishingSpot>();
foreach (var f in fp)
{
    uint it = (uint)f.Item.RowId;
    if (it == 0 || it >= 1000000) continue;
    var sp = fs.GetRowOrDefault(f.FishingSpot.RowId);
    string spotName = sp is { } s ? T(pn.GetRowOrDefault(s.PlaceName.RowId)?.Name) : "";
    gathers.Add(new
    {
        itemId = it,
        name = IName(it),
        discipline = "Fisher",
        gatheringType = (object)new[] { sp?.FishingSpotCategory == 1 ? "Spearfishing" : "Fishing" },
        levelRequired = (int)(sp?.GatheringLevel ?? 0),
        stars = (int)f.OceanStars,
        perceptionRequired = 0,
        isHidden = f.IsHidden,
        nodeLocations = spotName.Length > 0 ? (object)new[] { spotName } : U,
        isCollectable = U,
        isTimedNode = U,
        isLegendaryNode = U
    });
}
Write("gatherables.json",
    "Every gatherable item (Miner/Botanist) and fish (Fisher), with node type, required level and locations.",
    "PARTIAL - isCollectable, isTimedNode and isLegendaryNode are ??? for EVERY entry; none of the three "
  + "is expressed in a way this generator reads. ~100 Miner/Botanist items also have no gatheringType "
  + "(timed/Diadem/Island nodes) - treat that as unknown, NOT as 'not gatherable'.",
    new[] { "isCollectable", "isTimedNode", "isLegendaryNode" },
    gathers, gathers.Count);

// ---------------- 5. GEAR ----------------
var bp  = gd.GetExcelSheet<BaseParam>();
var esc = gd.GetExcelSheet<EquipSlotCategory>();
var iuc = gd.GetExcelSheet<ItemUICategory>();
var craftable = new HashSet<uint>();
foreach (var r in rec) if (r.ItemResult.RowId != 0) craftable.Add(r.ItemResult.RowId);

string SlotName(EquipSlotCategory c) =>
      c.MainHand == 1 ? "MainHand" : c.OffHand == 1 ? "OffHand" : c.Head == 1 ? "Head"
    : c.Body == 1 ? "Body" : c.Gloves == 1 ? "Hands" : c.Waist == 1 ? "Waist"
    : c.Legs == 1 ? "Legs" : c.Feet == 1 ? "Feet" : c.Ears == 1 ? "Ears"
    : c.Neck == 1 ? "Neck" : c.Wrists == 1 ? "Wrists"
    : (c.FingerL == 1 || c.FingerR == 1) ? "Ring"
    : c.SoulCrystal == 1 ? "SoulCrystal" : U;

var gear = new List<object>();
foreach (var i in items)
{
    if (T(i.Name).Length == 0 || i.EquipSlotCategory.RowId == 0) continue;
    var stats = new List<object>();
    for (int k = 0; k < i.BaseParam.Count; k++)
    {
        var p = i.BaseParam[k];
        short val = i.BaseParamValue[k];
        if (p.RowId == 0 || val == 0) continue;
        stats.Add(new { param = T(bp.GetRowOrDefault(p.RowId)?.Name), value = (int)val });
    }
    gear.Add(new
    {
        itemId = i.RowId,
        name = T(i.Name),
        slot = esc.GetRowOrDefault(i.EquipSlotCategory.RowId) is { } c ? SlotName(c) : U,
        uiCategory = T(iuc.GetRowOrDefault(i.ItemUICategory.RowId)?.Name),
        levelRequired = (int)i.LevelEquip,
        itemLevel = i.LevelItem.RowId,
        jobs = T(cjc.GetRowOrDefault(i.ClassJobCategory.RowId)?.Name),
        rarity = (int)i.Rarity,
        isUnique = i.IsUnique,
        isUntradable = i.IsUntradable,
        stats,
        expansion = U,
        acquisition = craftable.Contains(i.RowId) ? "Crafted" : U
    });
}
Write("gear.json",
    "Every equippable item with slot, required level, jobs and stats.",
    "PARTIAL - 'expansion' is ??? for EVERY entry (Item carries no ExVersion). 'acquisition' is only "
  + "known for crafted items; dungeon drops, relic chains, tomestone gear, raid drops and vendor gear "
  + "are all ??? and need external data.",
    new[] { "expansion", "acquisition (except Crafted)" },
    gear, gear.Count);

// ---------------- 6. FATES ----------------
var fate = gd.GetExcelSheet<Fate>();
var lvlSheet = gd.GetExcelSheet<Level>();
var fates = new List<object>();
foreach (var f in fate)
{
    string n = T(f.Name);
    if (n.Length == 0) continue;

    string zone = U;
    if (f.Location != 0 && lvlSheet.GetRowOrDefault(f.Location) is { } lv && lv.Territory.RowId != 0)
        zone = Zone(lv.Territory.RowId);

    uint rewardItem = f.EventItem.RowId;

    fates.Add(new
    {
        id = f.RowId,
        name = n,
        objective = T(f.Objective),
        description = T(f.Description),
        levelMin = (int)f.ClassJobLevel,
        levelMax = (int)f.ClassJobLevelMax,
        zone,
        locationLevelRow = f.Location,
        chain = new
        {
            isChained = f.FATEChain != 0,
            chainId = f.FATEChain,
            note = f.FATEChain != 0 ? "FATEChain groups linked FATEs; ordering within a chain is ???" : ""
        },
        requiredQuest = f.RequiredQuest.RowId != 0
            ? T(quest.GetRowOrDefault(f.RequiredQuest.RowId)?.Name) : "",
        eventItemReward = rewardItem != 0 ? (object)rewardItem : "",
        isSpecialFate = f.SpecialFate,
        isEurekaFate = f.EurekaFate != 0,
        monsters = U,
        monsterAbilities = U,
        rewards = new { bronze = U, silver = U, gold = U, itemReward = U }
    });
}
Write("fates.json",
    "Every named FATE, with level range, zone, and whether it belongs to a FATE chain.",
    "PARTIAL - 'monsters', 'monsterAbilities' and all of 'rewards' (bronze/silver/gold/itemReward) are "
  + "??? for EVERY entry; FATE spawn tables and reward tiers are not in the client sheets. "
  + "'chain.isChained' comes from Fate.FATEChain and IS game data, but the ORDER of FATEs within a "
  + "chain is ???. 'eventItemReward' is a raw EventItem row id where present, not a normal item.",
    new[] { "monsters", "monsterAbilities", "rewards.*", "chain ordering" },
    fates, fates.Count);

// ---------------- 7. NPCs ----------------
// Equipment resolution is the Glamourer-validated algorithm from research/Game Data Cookbook.md
// section 5: inline ENpcBase models layered OVER NpcEquip per slot, and an item lookup keyed by
// (slot, model, variant) because model ids are only unique within a slot.
var enpcRes = gd.GetExcelSheet<ENpcResident>();
var enpcBase = gd.GetExcelSheet<ENpcBase>();
var npcEquip = gd.GetExcelSheet<NpcEquip>();
var lvlAll = gd.GetExcelSheet<Level>();
var stain = gd.GetExcelSheet<Stain>();
var raceSheet = gd.GetExcelSheet<Race>();
var tribeSheet = gd.GetExcelSheet<Tribe>();

var modelMap = new Dictionary<(string, ushort, ushort), string>();
foreach (var it in items)
{
    if (T(it.Name).Length == 0) continue;
    ulong m = it.ModelMain;
    if (m == 0) continue;
    var c = it.EquipSlotCategory.ValueNullable;
    if (c is null) continue;
    string slot = c.Value.Head == 1 ? "Head" : c.Value.Body == 1 ? "Body"
                : c.Value.Gloves == 1 ? "Hands" : c.Value.Legs == 1 ? "Legs"
                : c.Value.Feet == 1 ? "Feet" : c.Value.Ears == 1 ? "Ears"
                : c.Value.Neck == 1 ? "Neck" : c.Value.Wrists == 1 ? "Wrists"
                : (c.Value.FingerL == 1 || c.Value.FingerR == 1) ? "Ring"
                : c.Value.MainHand == 1 ? "Main" : c.Value.OffHand == 1 ? "Off" : "";
    if (slot.Length == 0) continue;
    var key = (slot, (ushort)(m & 0xFFFF), (ushort)((m >> 16) & 0xFFFF));
    if (!modelMap.ContainsKey(key)) modelMap[key] = T(it.Name);
}

string LookSlot(string slot, uint model)
{
    ushort id = (ushort)(model & 0xFFFF), v = (ushort)((model >> 16) & 0xFFFF);
    if (id == 0) return "Nothing";
    if (id == 65535) return "hidden";
    return modelMap.TryGetValue((slot, id, v), out var n) ? n : $"Unknown ({id}-{v})";
}
string DyeName(uint id) => id == 0 ? "" : T(stain.GetRowOrDefault(id)?.Name);

// NPC row id -> every placement
var placements = new Dictionary<uint, List<Level>>();
foreach (var l in lvlAll)
{
    if (l.Object.RowId < 1000000 || l.Territory.RowId == 0) continue;
    if (!placements.TryGetValue(l.Object.RowId, out var lst)) { lst = new(); placements[l.Object.RowId] = lst; }
    lst.Add(l);
}

var npcs = new List<object>();
foreach (var n in enpcRes)
{
    string name = T(n.Singular);
    if (name.Length == 0) continue;
    var bOpt = enpcBase.GetRowOrDefault(n.RowId);
    if (bOpt is null) continue;
    var b = bOpt.Value;
    var e = npcEquip.GetRowOrDefault(b.NpcEquip.RowId);

    uint Pick(uint inline, Func<NpcEquip, uint> fromSet)
        => inline != 0 ? inline : (e is { } ee ? fromSet(ee) : 0u);
    uint PickDye(uint inline, Func<NpcEquip, uint> fromSet)
        => inline != 0 ? inline : (e is { } ee ? fromSet(ee) : 0u);

    var slots = new (string label, string slot, uint model, uint dye)[]
    {
        ("head",      "Head",   Pick(b.ModelHead,      x => x.ModelHead),      PickDye(b.DyeHead.RowId,      x => x.DyeHead.RowId)),
        ("body",      "Body",   Pick(b.ModelBody,      x => x.ModelBody),      PickDye(b.DyeBody.RowId,      x => x.DyeBody.RowId)),
        ("hands",     "Hands",  Pick(b.ModelHands,     x => x.ModelHands),     PickDye(b.DyeHands.RowId,     x => x.DyeHands.RowId)),
        ("legs",      "Legs",   Pick(b.ModelLegs,      x => x.ModelLegs),      PickDye(b.DyeLegs.RowId,      x => x.DyeLegs.RowId)),
        ("feet",      "Feet",   Pick(b.ModelFeet,      x => x.ModelFeet),      PickDye(b.DyeFeet.RowId,      x => x.DyeFeet.RowId)),
        ("ears",      "Ears",   Pick(b.ModelEars,      x => x.ModelEars),      PickDye(b.DyeEars.RowId,      x => x.DyeEars.RowId)),
        ("neck",      "Neck",   Pick(b.ModelNeck,      x => x.ModelNeck),      PickDye(b.DyeNeck.RowId,      x => x.DyeNeck.RowId)),
        ("wrists",    "Wrists", Pick(b.ModelWrists,    x => x.ModelWrists),    PickDye(b.DyeWrists.RowId,    x => x.DyeWrists.RowId)),
        ("rightRing", "Ring",   Pick(b.ModelRightRing, x => x.ModelRightRing), PickDye(b.DyeRightRing.RowId, x => x.DyeRightRing.RowId)),
        ("leftRing",  "Ring",   Pick(b.ModelLeftRing,  x => x.ModelLeftRing),  PickDye(b.DyeLeftRing.RowId,  x => x.DyeLeftRing.RowId)),
        ("mainHand",  "Main",   Pick((uint)b.ModelMainHand, x => (uint)x.ModelMainHand), PickDye(b.DyeMainHand.RowId, x => x.DyeMainHand.RowId)),
        ("offHand",   "Off",    Pick((uint)b.ModelOffHand,  x => (uint)x.ModelOffHand),  PickDye(b.DyeOffHand.RowId,  x => x.DyeOffHand.RowId)),
    };
    // Only NON-EMPTY slots are emitted. A slot absent from "equipment" is empty — that is
    // documented in the header. Serialising all twelve for all 30,878 NPCs produced a 60 MB file,
    // past GitHub's 50 MB warning threshold, and ~70% of it was the word "Nothing".
    var equipment = new Dictionary<string, object>();
    foreach (var s in slots)
    {
        string item = LookSlot(s.slot, s.model);
        if (item == "Nothing") continue;
        string dye = DyeName(s.dye);
        equipment[s.label] = item.StartsWith("Unknown (")
            ? new { item, modelId = (object)s.model, dye }   // keep the id: it is the only handle
            : (object)new { item, dye };
    }

    var locs = new List<object>();
    if (placements.TryGetValue(n.RowId, out var pls))
        foreach (var l in pls)
        {
            var tt = terr.GetRowOrDefault(l.Territory.RowId);
            locs.Add(new
            {
                territoryId = l.Territory.RowId,
                zone = Zone(l.Territory.RowId),
                x = MathF.Round(l.X, 1),
                y = MathF.Round(l.Y, 1),
                z = MathF.Round(l.Z, 1),
                inInstance = tt is { } t2 && t2.ContentFinderCondition.RowId != 0,
                expansion = tt is { } t3 ? T(exv.GetRowOrDefault(t3.ExVersion.RowId)?.Name) : U
            });
        }

    npcs.Add(new
    {
        id = n.RowId,
        name,
        locations = locs.Count > 0 ? (object)locs : U,
        race = T(raceSheet.GetRowOrDefault(b.Race.RowId)?.Masculine),
        tribe = T(tribeSheet.GetRowOrDefault(b.Tribe.RowId)?.Masculine),
        gender = b.Gender == 0 ? "male" : "female",
        hairStyleId = (int)b.HairStyle,
        hairColorIndex = (int)b.HairColor,
        hairColorName = U,
        skinColorIndex = (int)b.SkinColor,
        eyeColorIndex = (int)b.EyeColor,
        modelChara = b.ModelChara.RowId,
        npcEquipRow = b.NpcEquip.RowId,
        level = U,
        isTargetable = U,
        equipment
    });
}
Write("npcs.json",
    "Every named event NPC with resolved equipment, placements, race/tribe/gender and appearance ids.",
    "PARTIAL - 'level' and 'isTargetable' are ??? for EVERY entry (neither is in client data). "
  + "A slot ABSENT from 'equipment' is EMPTY (omitted to keep the file a sane size); ??? is never "
  + "used inside equipment. "
  + "'hairColorName' is ??? because the colour palette lives in chara/xls/charamake/human.cmp, a raw "
  + "file with no Excel sheet - only the raw hairColorIndex is available. Equipment reading "
  + "'Unknown (id-variant)' means NPC-exclusive gear with no player-equippable item, which is normal, "
  + "not an error. NPCs with no Level row have locations=??? (instanced//cutscene-only spawns).",
    new[] { "level", "isTargetable", "hairColorName", "locations (when ???)" },
    npcs, npcs.Count);

Console.WriteLine("\ndone.");
