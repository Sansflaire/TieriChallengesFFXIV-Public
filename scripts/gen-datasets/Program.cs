using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Lumina;
using Lumina.Excel.Sheets;

const string U = "???";
var OUT = @"C:\Users\trist\AppData\Roaming\XIVLauncher\devPlugins\TieriChallengesFFXIV\data";
Directory.CreateDirectory(OUT);

var gd = new GameData(@"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack");
var JO = new JsonSerializerOptions { WriteIndented = true };

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

void Write(string file, string desc, string? needs, string[] unknown, object entries, int count)
{
    var path = Path.Combine(OUT, file);
    File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        generated = DateTime.UtcNow.ToString("o"),
        source = "FFXIV sqpack via Lumina. Regenerate: see data/README.md",
        description = desc,
        needsVerification = needs,
        unknownFields = unknown,
        unknownMarker = U,
        count,
        entries
    }, JO));
    Console.WriteLine($"  {file,-30} {count,6} entries  {new FileInfo(path).Length / 1024,7} KB");
}

Console.WriteLine("writing:");

// ---------------- 1. DUTIES ----------------
var cfc = gd.GetExcelSheet<ContentFinderCondition>();
var ctype = gd.GetExcelSheet<ContentType>();
var duties = new List<object>();
foreach (var c in cfc)
{
    if (T(c.Name).Length == 0) continue;
    uint ct = c.ContentType.RowId;
    if (ct != 2 && ct != 4 && ct != 5 && ct != 28 && ct != 37) continue;
    string kind = ct == 2 ? "Dungeon" : ct == 4 ? "Trial" : ct == 28 ? "Ultimate"
                : ct == 37 ? "Chaotic Alliance Raid"
                : (c.QueueMaxPlayers >= 24 ? "Alliance Raid" : "Normal Raid");

    bool recorded = c.UnlockCriteria.RowId != 0;
    string questName = U;
    if (recorded && c.UnlockType == 1)
    {
        var q = T(quest.GetRowOrDefault(c.UnlockCriteria.RowId)?.Name);
        if (q.Length > 0) questName = q;
    }

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
        itemLevelRequired = (int)c.ItemLevelRequired,
        partySize = (int)c.QueueMaxPlayers,
        highEndDuty = c.HighEndDuty,
        unlock = new
        {
            recordedInGameData = recorded,
            unlockType = (int)c.UnlockType,
            criteriaId = c.UnlockCriteria.RowId,
            quest = questName,
            note = recorded ? "" : "NOT in game data - use live UIState/QuestManager"
        },
        monsters = U,
        itemsFound = U
    });
}
Write("duties.json",
    "Dungeons, trials, normal/alliance raids, ultimates and chaotic alliance raids.",
    "PARTIAL - 'monsters' and 'itemsFound' are ??? for EVERY entry and must be compiled manually. "
  + "'unlock.recordedInGameData=false' means the game files hold no unlock criteria (754 of 857 duties); "
  + "those need live UIState/QuestManager or external data.",
    new[] { "monsters", "itemsFound", "unlock (when recordedInGameData=false)" },
    duties, duties.Count);

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
    "NEEDS VERIFICATION - THE ENTIRE FILE. level, drops, abilities, mapLocation and inInstance are ??? "
  + "for every single entry, and zones exist for only ~259 of them (the Hunting Log, 1.8%). "
  + "Mob loot tables are server-side and absent from client data. All of this must come from external "
  + "sources - see TODO A4.",
    new[] { "level", "drops", "abilities", "mapLocation", "inInstance", "zones (~98% of entries)" },
    mons, mons.Count);

// ---------------- 3. LEVEL-BASED RECIPES ----------------
var rec = gd.GetExcelSheet<Recipe>();
var rlt = gd.GetExcelSheet<RecipeLevelTable>();
var ctr = gd.GetExcelSheet<CraftType>();
var recs = new List<object>();
foreach (var r in rec)
{
    if (r.ItemResult.RowId == 0) continue;
    if (r.SecretRecipeBook.RowId != 0 || r.IsSpecializationRequired || r.IsExpert
        || r.Quest.RowId != 0 || r.StatusRequired.RowId != 0 || r.ItemRequired.RowId != 0) continue;

    var lt = rlt.GetRowOrDefault(r.RecipeLevelTable.RowId);
    var ing = new List<object>();
    for (int k = 0; k < r.Ingredient.Count; k++)
    {
        var g = r.Ingredient[k];
        byte a = r.AmountIngredient[k];
        if (g.RowId == 0 || a == 0) continue;
        ing.Add(new { itemId = g.RowId, name = IName(g.RowId), amount = (int)a });
    }
    recs.Add(new
    {
        recipeId = r.RowId,
        craftType = T(ctr.GetRowOrDefault(r.CraftType.RowId)?.Name),
        resultItemId = r.ItemResult.RowId,
        resultName = IName(r.ItemResult.RowId),
        resultAmount = (int)r.AmountResult,
        recipeLevel = (int)(lt?.ClassJobLevel ?? 0),
        stars = (int)(lt?.Stars ?? 0),
        difficulty = (int)(lt?.Difficulty ?? 0),
        durability = (int)(lt?.Durability ?? 0),
        canHq = r.CanHq,
        canQuickSynth = r.CanQuickSynth,
        unlock = new
        {
            type = "level",
            classJobLevel = (int)(lt?.ClassJobLevel ?? 0),
            book = (string?)null,
            note = "plain level-based: no book, quest, status, held-item or specialist requirement"
        },
        ingredients = ing
    });
}
Write("recipes-level-based.json",
    "Plain level-based recipes ONLY. Master/Expert/Specialist/quest/status/item-gated recipes are "
  + "excluded by construction, so every entry here is safe for a generated quest.",
    null, Array.Empty<string>(), recs, recs.Count);

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

Console.WriteLine("\ndone.");
