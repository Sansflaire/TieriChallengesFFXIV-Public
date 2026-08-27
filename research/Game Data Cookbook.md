# Game Data Cookbook — how to actually find things

Working recipes for reading FFXIV game data, with the traps that produced wrong answers on the
first attempt. Every snippet here was **run and verified** on 2026-08-26, and the NPC-equipment
recipe is validated against Glamourer.

Companion to [`Lumina Sheet Findings.md`](Lumina%20Sheet%20Findings.md), which records *findings*.
This file records *method*.

---

## SECTION INDEX

| # | Section | Anchor |
|---|---------|--------|
| 0 | Setup — reading game data with no client | `setup` |
| 1 | Items | `items` |
| 2 | Craftability and ingredients | `craft` |
| 3 | Monsters | `monsters` |
| 4 | NPCs and where they stand | `npcs` |
| 5 | **NPC equipment — the full correct algorithm** | `equip` |
| 6 | Is this territory an instance? | `instance` |
| 6A | **Unlocks — sheets can't answer these** | `unlocks` |
| 6B | Recipe level, fish, gear requirements | `reqs` |
| 7 | Traps, collected | `traps` |

---

<!-- SECTION:setup -->
## 0. Setup — no game client required

Lumina opens `sqpack` straight off disk. A throwaway console app answers most questions in
about a second, which is almost always faster than arranging an in-game test.

```xml
<Reference Include="Lumina">       <HintPath>$(APPDATA)\XIVLauncher\addon\Hooks\dev\Lumina.dll</HintPath></Reference>
<Reference Include="Lumina.Excel"> <HintPath>$(APPDATA)\XIVLauncher\addon\Hooks\dev\Lumina.Excel.dll</HintPath></Reference>
```

```csharp
var gd = new GameData(@"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack");
var items = gd.GetExcelSheet<Item>();      // 52,801 rows
```

**Use `Lumina.Excel.Sheets`, never `Lumina.Excel.Sheets.Experimental`** — both exist with 1,198
types each, `GetExcelSheet<T>` is generic, and an IDE auto-import binds the wrong one silently.

Inside the plugin the same sheets come from `Plugin.DataManager.GetExcelSheet<T>()`. Subrow
sheets (e.g. `GilShopItem`) need `GetSubrowExcelSheet<T>()` instead.

**Reach for this before scheduling anything in-game.** Schema and row data are both offline;
only *live player/actor state* genuinely needs the client.

<!-- SECTION:items -->
## 1. Items

```csharp
var items = gd.GetExcelSheet<Item>();                       // 52,801 rows, 50,773 named
var byName = items.FirstOrDefault(i => i.Name.ToString() == "Iron Ingot");
var byId   = items.GetRow(5057);
```

Useful columns: `Name`, `LevelItem` (ilvl), `EquipSlotCategory`, `ModelMain`/`ModelSub`,
`ItemUICategory`, `IsUntradable`, `StackSize`, `ClassJobCategory`.

**~2,000 rows are blank padding.** Always filter `Name.ToString().Length > 0`.

<!-- SECTION:craft -->
## 2. Craftability and ingredients

**Is an item craftable?** `Recipe.ItemResult` is the authority — build the set once:

```csharp
var craftable = new HashSet<uint>();
foreach (var r in gd.GetExcelSheet<Recipe>())
    if (r.ItemResult.RowId != 0) craftable.Add(r.ItemResult.RowId);

bool canCraft = craftable.Contains(itemId);
```

Measured: **14,909 recipes → 11,341 distinct craftable items**, out of 50,773 named items. So
roughly **22% of named items are craftable**, and a set membership test is the whole answer.

An item can have **several** recipes (different crafting classes), so `Recipe` rows outnumber
craftable items. Use `RecipeLookup` to go item → recipe per `CraftType`.

**What does a craft need?**

```csharp
for (int k = 0; k < recipe.Ingredient.Count; k++)
{
    var ing = recipe.Ingredient[k];
    byte amt = recipe.AmountIngredient[k];        // PARALLEL collection
    if (ing.RowId == 0 || amt == 0) continue;     // slots are fixed-width and padded
    Console.WriteLine($"{amt} x {items.GetRow(ing.RowId).Name}");
}
```

Verified against a known recipe — **Iron Ingot x1 = 3 × Iron Ore + 1 × Fire Shard.** Correct.

⚠️ **`AmountIngredient` is index-parallel to `Ingredient`.** Same shape as `MonsterNote.Count`
and the GlamourDresser bit spans. Iterate by index; never zip them independently, and skip the
padded empty slots.

Other useful columns: `AmountResult`, `CraftType` (which class), `RecipeLevelTable` (difficulty).


### Recipe KIND — level-based vs Master vs Expert

A daily/weekly Craft route must only ever use a **plain level-based** recipe. `Recipe` carries
every discriminator needed; there is no single "kind" column, so it is a conjunction:

```csharp
bool IsPlainLevelRecipe(Recipe r) =>
       r.SecretRecipeBook.RowId == 0     // not a Master / recipe-book recipe
    && !r.IsSpecializationRequired       // not Specialist-only
    && !r.IsExpert                       // not an Expert recipe
    && r.Quest.RowId == 0                // not unlocked by a quest
    && r.StatusRequired.RowId == 0       // no required status effect
    && r.ItemRequired.RowId == 0;        // no required held item
```

Measured over 13,892 recipes with a result:

| Filter | Count | Meaning |
|---|---|---|
| `SecretRecipeBook != 0` | 3,459 | Master / recipe-book |
| `IsExpert` | 544 | Expert (e.g. Skybuilders') |
| `ItemRequired != 0` | 400 | needs a specific held item |
| `StatusRequired != 0` | 312 | needs a status effect |
| `Quest != 0` | 72 | quest-unlocked |
| `IsSpecializationRequired` | 19 | Specialist only |
| **PLAIN (all clear)** | **9,577 recipes → 7,516 distinct items** | **safe for generated quests** |

Spot-checked: plain → *Peisteskin Harness* (Leatherworking); master → *Heavy Wolfram Helm*;
expert → *Grade 2 Artisanal Skybuilders' Wardrobe*. Matches expectation.

`CraftType` gives the class (Leatherworking, Blacksmith, …) and is **separate** from kind — a
Leatherworker-specific item is fine, a Master Leatherworker recipe is not.

### Gathered items — Miner / Botanist / Fisher

Two disjoint sources; there is no single "gatherable" flag:

```csharp
var gathered = new HashSet<uint>();
foreach (var g in gd.GetExcelSheet<GatheringItem>())  gathered.Add((uint)g.Item.RowId);  // MIN + BTN
foreach (var f in gd.GetExcelSheet<FishParameter>())  gathered.Add((uint)f.Item.RowId);  // FSH
```

| Source | Rows | Distinct items | Jobs |
|---|---|---|---|
| `GatheringItem` | 2,148 | **1,596** | Miner + Botanist |
| `FishParameter` | 2,512 | **2,461** | Fisher |
| union | — | **4,039** | all three |

**Filter `RowId != 0 && RowId < 1,000,000`** — both sheets contain padding and out-of-range refs.

**Which job?** `GatheringItem` does not say. Walk `GatheringPointBase.Item` → `GatheringType`:

| GatheringType | Items | Job |
|---|---|---|
| Harvesting | 542 | Botanist |
| Logging | 334 | Botanist |
| Mining | 447 | Miner |
| Quarrying | 309 | Miner |

⚠️ Only **1,495 of 1,596** map to a node type — roughly 100 gathering items sit on no ordinary
node (timed/legendary, Diadem, Island Sanctuary). Treat "no GatheringType" as *unknown*, never as
*not gatherable*.

Fisher has no equivalent type split here; `FishingSpot` locates fish if that is ever needed.

<!-- SECTION:monsters -->
## 3. Monsters

```csharp
var bnpc = gd.GetExcelSheet<BNpcName>();     // 15,001 rows, 14,560 named
```

**Listing them is easy. Locating them is mostly not.**

| Source | Coverage | Precision |
|---|---|---|
| `MonsterNoteTarget.PlaceNameZone` | **259 mobs — 1.8% of named** | zone + sub-location |
| Territory **LGB** layer files | in principle all spawns | exact coordinates |

The Hunting Log covers only the low-level mobs it was written for. **It is not a general
monster-location index** — treat 1.8% as the ceiling, not a starting point.

```csharp
foreach (var t in gd.GetExcelSheet<MonsterNoteTarget>())
{
    if (t.BNpcName.RowId == 0) continue;
    var zones = t.PlaceNameZone.Where(z => z.RowId != 0);   // often empty
}
```

**There is no mob → item link at all** (see Lumina Sheet Findings §2 — every `BNpc*` sheet has
zero item references). Loot is server-side. Item→mob mapping must come from curated external
data.

<!-- SECTION:npcs -->
## 4. NPCs and where they stand

```csharp
var enpc = gd.GetExcelSheet<ENpcResident>();  // 59,851 — names
var eb   = gd.GetExcelSheet<ENpcBase>();      // 59,851 — appearance + gear, SAME row ids
var lvl  = gd.GetExcelSheet<Level>();         // 61,346 — placements
```

`Level` is the placement sheet: `X` `Y` `Z` `Yaw` `Radius` + `Territory` + `Map` + `Object`.

```csharp
// every placement of a named NPC
foreach (var l in lvl.Where(l => l.Object.RowId == npcRowId && l.Territory.RowId != 0))
{
    var t    = terr.GetRow(l.Territory.RowId);
    var zone = pn.GetRow(t.PlaceName.RowId).Name;
    // l.X, l.Y, l.Z are WORLD coordinates
}
```

**`ENpcResident` and `ENpcBase` share row ids** — look both up with the same number.
NPC row ids start at 1,000,000; `Level.Object` also references non-NPC objects, so filter.

An NPC may appear in **several** territories (the Apartment Merchant exists in every apartment
lobby as separate rows), so match on the one you want, not `First()`.

<!-- SECTION:equip -->
## 5. NPC equipment — the full correct algorithm

**Validated against Glamourer on all 10 displayed slots, dye included.** Two independent traps
here; both produced confidently wrong output on the first attempt.

### Trap A — the two equipment sources LAYER

`ENpcBase` has inline `Model*` columns **and** an `NpcEquip` row ref. These are not
alternatives. Resolve **per slot**:

```csharp
uint Pick(uint inline, uint fromEquipSet) => inline != 0 ? inline : fromEquipSet;
```

The Apartment Merchant (`ENpcBase 1018091`) has `ModelHead` inline **and** `NpcEquip = 192`
carrying everything else. Choosing one source reported five empty slots on a dressed NPC.

### Trap B — the item lookup MUST include the slot

Model ids are only unique *within a slot*. Model `6/1` is **both** *Dated Straw Hat* and
*Dated Taupe Sheepskin Jerkin*. A map keyed on `(model, variant)` put a straw hat on a torso.

```csharp
// key: (slot, modelId, variant)
var map = new Dictionary<(string, ushort, ushort), string>();
foreach (var it in items)
{
    if (it.Name.ToString().Length == 0) continue;
    ulong m = it.ModelMain; if (m == 0) continue;
    var c = it.EquipSlotCategory.ValueNullable; if (c is null) continue;

    string slot = c.Value.Head   == 1 ? "Head"
                : c.Value.Body   == 1 ? "Body"
                : c.Value.Gloves == 1 ? "Hands"
                : c.Value.Legs   == 1 ? "Legs"
                : c.Value.Feet   == 1 ? "Feet"
                : c.Value.Ears   == 1 ? "Ears"
                : c.Value.Neck   == 1 ? "Neck"
                : c.Value.Wrists == 1 ? "Wrists"
                : (c.Value.FingerL == 1 || c.Value.FingerR == 1) ? "Ring"
                : c.Value.MainHand == 1 ? "Main"
                : c.Value.OffHand  == 1 ? "Off" : null;
    if (slot is null) continue;

    var key = (slot, (ushort)(m & 0xFFFF), (ushort)((m >> 16) & 0xFFFF));
    if (!map.ContainsKey(key)) map[key] = it.Name.ToString();
}
```

Both rings share one model space → a single `"Ring"` key.

### Reading a slot

```csharp
string Look(string slot, uint model)
{
    ushort id = (ushort)(model & 0xFFFF), v = (ushort)((model >> 16) & 0xFFFF);
    if (id == 0)     return "Nothing";
    if (id == 65535) return "hidden";        // explicitly hidden, NOT empty
    return map.TryGetValue((slot, id, v), out var n) ? n : $"Unknown ({id}-{v})";
}
```

**"Unknown" is a normal result, not a failure** — much NPC gear has no player-equippable
equivalent. Glamourer prints `Unknown (28-90)` for the same head, by the same convention.

### All twelve slots

`Head Body Hands Legs Feet Ears Neck Wrists RightRing LeftRing MainHand OffHand`, each with a
matching `Dye*` (and `Dye2*` for the second dye channel), resolved with the same `Pick`.

**Do not stop at the five visible ones.** An earlier pass checked Head/Body/Hands/Legs/Feet and
reported "6 of 6 matching" — the five accessory slots had never been queried at all.

### Live actors

For an NPC on screen, the sheet gives the *default* appearance. Instanced or story-swapped NPCs
differ. The live route reads the same equipment block off any actor's `Character` struct — the
plugin already does this for the player in `PlayerStateReader.ReadEquipment`. Not yet exercised
against a non-player actor.

<!-- SECTION:instance -->
## 6. Is this territory an instance?

```csharp
var t = gd.GetExcelSheet<TerritoryType>().GetRow(territoryId);
bool isDuty = t.ContentFinderCondition.RowId != 0;
```

Measured:

| Territory | CFC | IntendedUse | ExclusiveType | Instance? |
|---|---|---|---|---|
| New Gridania (132) | 0 | 0 | 0 | no — city |
| South Shroud (153) | 0 | 1 | 0 | no — overworld |
| Lily Hills Apartment Lobby (574) | 0 | 14 | 0 | no — housing |
| **Sastasha (1036)** | **4** | 3 | 2 | **yes — dungeon** |

**`ContentFinderCondition.RowId != 0` is the test.** It also gives the duty's name, level
requirement and party size. `TerritoryIntendedUse` classifies further (13/14 = housing, already
used by `AttunementService.IsZoneSpoilered`).

**Live, in the plugin, prefer the condition flag:**

```csharp
Plugin.Condition[ConditionFlag.BoundByDuty]      // already modelled as GameStateFlag.BoundByDuty
```

That catches solo duties and cutscene lockouts the sheet lookup alone would miss. Use the sheet
to ask "*is this territory* a duty", the flag to ask "*is the player currently* bound by one".


<!-- SECTION:unlocks -->
## 6A. Unlocks — the sheets CANNOT answer these

**Unlock state is per-player, so it is live-only, always.** No sheet knows what *this* player has
done. The sheets can at best say *which* quest gates something; only the running client says
whether it was completed.

### Duties (dungeons, raids, Occult Crescent…)

`ContentFinderCondition.UnlockCriteria` + `UnlockType` — **and it is badly incomplete**:

```
duties named: 857        with an UnlockCriteria: 102
UnlockType:   0 × 754    1 × 100    2 × 1    3 × 1    4 × 1
```

| Duty | UnlockType | UnlockCriteria |
|---|---|---|
| Sastasha | 0 | **0 — nothing recorded** |
| Copperbell Mines | 0 | **0 — nothing recorded** |
| the Occult Crescent: South Horn | 1 | 70847 → a `Quest` row |
| the Occult Crescent: North Horn | 1 | 71047 → a `Quest` row |

So **88% of duties record no unlock criteria at all.** Occult Crescent happens to be one of the
100 that do (`UnlockType == 1` ⇒ `UnlockCriteria` is a `Quest` row id). Ordinary dungeons like
Sastasha are not. `UnlockCriteria` is an **untyped `RowRef`** whose target sheet depends on
`UnlockType`, so never resolve it without checking the type first.

**Conclusion: do not build unlock logic on this sheet.** Use the live APIs.

### The live APIs — all verified present in the installed FFXIVClientStructs

| Question | API |
|---|---|
| Is this duty/content unlocked? | `UIState.IsUnlockLinkUnlocked`, `UIState.IsUnlockLinkUnlockedOrQuestCompleted` |
| Is a quest done? | `QuestManager.IsQuestComplete` |
| Is a Master book owned? | `PlayerState.IsSecretRecipeBookUnlocked` |

⚠️ Verified only that these **symbols exist** in the DLL — signatures and semantics are not
exercised. Confirm against a live character before shipping a gate on any of them.

**Is a recipe available to the player?** Three independent conditions, all live:
1. `PlayerState.IsSecretRecipeBookUnlocked(book)` if `Recipe.SecretRecipeBook != 0`
2. crafting class level ≥ `RecipeLevelTable.ClassJobLevel`
3. Specialist state if `IsSpecializationRequired`

`SecretRecipeBook` is tiny — **111 books**, and each *is an item*: `SecretRecipeBook.Item`
(book 1 "Master Carpenter I" → item 7778, same name).

<!-- SECTION:reqs -->
## 6B. Level and job requirements

### Recipe level — `RecipeLevelTable.ClassJobLevel`

```csharp
var t = gd.GetExcelSheet<RecipeLevelTable>().GetRow(recipe.RecipeLevelTable.RowId);
byte level = t.ClassJobLevel;    // also: Stars, Difficulty, Durability, Quality
```

Verified: *Acacia Ring* (Woodworking) `ClassJobLevel=96, stars=0`. **`ClassJobLevel` is the
required level; `RowId` is the internal recipe level and is NOT the same number.**

### Fish — what, where, and at what level

```csharp
var fp = gd.GetExcelSheet<FishParameter>();   // 2,512 rows → 2,461 distinct items
var fs = gd.GetExcelSheet<FishingSpot>();     // 576 spots
```

`FishParameter.FishingSpot` → `FishingSpot`, which carries `PlaceName`, `TerritoryType`,
`X`/`Z`, `Radius`, `Rare`, and **`GatheringLevel`** — the required Fisher level.

| Fish | Spot | Lvl | Terr |
|---|---|---|---|
| Malm Kelp | Limsa Lominsa Upper Decks | 1 | 128 |
| Crayfish | The Vein | 5 | 148 |
| Chub | Rogue River | 1 | 134 |

⚠️ **The level lives on the SPOT, not the fish** (`FishingSpot.GatheringLevel`).
`FishParameter.GatheringItemLevel` is a *convert-table* ref, not a level.
`FishingSpot.Item` is also a `Collection` — spot → many fish, the reverse direction.
`IsInLog` marks Fishing Log entries; `OceanStars` is ocean-fishing rarity.

### Gear — equip level and job

```csharp
item.LevelEquip                  // required character level
item.LevelItem.RowId             // item level (ilvl) — a DIFFERENT number
item.ClassJobCategory            // which jobs, as a named category
item.EquipSlotCategory.RowId != 0  // is it equippable at all → 28,992 items
```

| Item | LevelEquip | ilvl | Jobs |
|---|---|---|---|
| Woolen Cowl | 47 | 47 | All Classes |
| Aetherial Woolen Cowl | **45** | **47** | All Classes |
| Hetairos Mail | 50 | 60 | GLA MRD LNC PLD WAR DRG DRK GNB RPR |
| Strategos Bliaud | 50 | 60 | Disciple of Magic |

⚠️ **`LevelEquip` ≠ `LevelItem`** — the Aetherial Woolen Cowl is ilvl 47 but equippable at 45.
Using ilvl as a level gate is wrong.

`ClassJobCategory.Name` is a display string ("Disciple of Magic", "All Classes", or an explicit
job list). For a machine check, read that row's per-job booleans rather than parsing the name.

<!-- SECTION:traps -->
## 7. Traps, collected

1. **Parallel collections.** `Recipe.Ingredient`/`AmountIngredient`, `MonsterNote.MonsterNoteTarget`/`Count`, GlamourDresser ids/bits. Iterate by index; never independently.
2. **Fixed-width padded slots.** Recipe ingredient arrays contain empty entries — skip `RowId == 0 || amount == 0`.
3. **Model ids are slot-scoped.** Always key `(slot, model, variant)`.
4. **NPC equipment layers**, inline over `NpcEquip`, per slot.
5. **`65535` ≠ `0`.** Hidden vs empty.
6. **Two `Sheets` namespaces.** Never `Experimental`.
7. **Subrow sheets** need `GetSubrowExcelSheet<T>()`.
8. **Blank rows everywhere.** Filter on name length.
9. **Grepping a DLL matches substrings.** `ItemDrop` was `ItemDropRate` on a levequest struct. Reflect, don't grep.
10. **Check every slot before claiming a match.** Reporting "6 of 6" while silently skipping five slots is worse than reporting five.
11. **Unlock state is never in a sheet.** It is per-player and live-only. `ContentFinderCondition.UnlockCriteria` is empty for 754 of 857 duties, and is an untyped `RowRef` gated on `UnlockType`.
12. **`LevelEquip` ≠ `LevelItem`.** Required level and item level are different columns and routinely differ.
13. **Fishing level is on the SPOT**, not the fish — `FishingSpot.GatheringLevel`.
14. **Don't guess column names.** `ContentFinderCondition.UnlockQuest` does not exist (it is `UnlockCriteria`); the guess cost a build. Dump the columns first — §0 has the one-liner.
