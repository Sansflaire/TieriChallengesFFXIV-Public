# Lumina sheet findings — drops, Hunting Log, and a namespace trap

Method: reflection over the installed `addon/Hooks/dev/Lumina.Excel.dll` from a throwaway console
app. **No game session needed** — the assembly carries the full schema, so anything about sheet
*shape* is answerable offline. Only row *data* needs sqpack or a running client.

Dated 2026-08-26. Closes **R6** and **R2**.

---

## 1. `ItemDrop` does not exist — it was a substring false positive

**R6, answered.** My earlier binary `grep` for drop-table types reported a hit on `ItemDrop`, and
I recorded it as "a single match I didn't chase down; near-certainly not a loot table."

It is not a type at all. The match came from **`ItemDropRate`**, a `Byte` field on
`LeveDataStruct` and `CompanyLeveStructStruct` — the drop rate of a **levequest** item reward.
Nothing to do with monsters.

```
=== every type whose name contains 'Drop' ===
(none)
```

**Zero types in the entire assembly have "Drop" in the name.** Grepping a binary matches
substrings inside longer identifiers; only reflection distinguishes a type from a field of some
unrelated struct. Worth remembering — the binary grep was right that the string existed and wrong
about what it meant.

## 2. Mob loot is definitively not in the client sheets

**Q11, now verified exhaustively rather than by guessing names.** Earlier I checked four names I
thought of (`DropList`, `LootTable`, `BNpcDrop`, `MonsterDrop`) and found them absent. That is
weak evidence — absence of the names I imagined. This scanned **all 1,198 sheet types**:

- Keywords `drop`, `loot`, `spoil`, `booty`, `plunder`: **no matches at all.**
- Keyword `reward`/`treasure`: ~30 matches, and every one is a **scripted, deterministic** reward
  — `LeveRewardItem`, `InstanceContentRewardItem`, `CollectablesShopRewardItem`,
  `GCSupplyDutyReward`, `WKSTreasure`. None is randomized mob loot.

The decisive check — **every `BNpc*` sheet carries zero item references**:

| Sheet | Columns | Item refs |
|---|---|---|
| `BNpcBase` | 30 | **NONE** |
| `BNpcName` | 11 | **NONE** |
| `BNpcCustomize` | 29 | **NONE** |
| `BNpcParts` | 70 | **NONE** |
| `BNpcResist` | 14 | **NONE** |
| `BNpcState` | 17 | **NONE** |

A mob row **cannot** point at an item in client data. Loot is server-side. This is now settled and
should not be re-investigated.

**Consequence (unchanged, now certain):** Hunt routes are kill-count only and cannot chain into
Craft. Material flow must run Gather→Craft or vendor→Craft. Curated external data (Trist's own
materials list) is the only route to item→mob mapping.

## 3. The Hunting Log gives mob → zone → count

**R2, answered without the game.** `MonsterNote` is the Hunting Log entry; `MonsterNoteTarget` is
one creature within it.

```
MonsterNote
  Name               ReadOnlySeString
  Reward             UInt32
  MonsterNoteTarget  Collection<RowRef<MonsterNoteTarget>>
  Count              Collection<Byte>          ← parallel to MonsterNoteTarget

MonsterNoteTarget
  BNpcName           RowRef<BNpcName>          ← which creature
  PlaceNameZone      Collection<RowRef<PlaceName>>   ← which zone(s)
  PlaceNameLocation  Collection<RowRef<PlaceName>>   ← where within them
  Town               RowRef<Town>
  Icon               Int32
```

**Everything a Hunt route needs resolves**, and better than hoped — targets carry *both* a zone
and a finer location, and multiple of each.

**The trap:** `Count` is a **parallel collection** to `MonsterNoteTarget`, exactly like
`GlamourDresserItemIds` / `GlamourDresserItemSetUnlockBits` in the parent CLAUDE.md. Index N of
`Count` belongs to index N of `MonsterNoteTarget`. Reading them independently, or assuming one
`Count` per note, silently produces wrong kill requirements. Verify the pairing on real rows
before shipping a generated Hunt quest.

Still unverified (needs row data, not schema): whether every target actually populates
`PlaceNameZone`, and whether `Count` is ever shorter than the target list.

## 4. ⚠️ There are TWO parallel sheet namespaces

```
Lumina.Excel.Sheets                 1198 types
Lumina.Excel.Sheets.Experimental    1198 types
```

Every sheet exists **twice**, including `Item`:

```
Lumina.Excel.Sheets.Item
Lumina.Excel.Sheets.Experimental.Item
```

This plugin uses `Lumina.Excel.Sheets` everywhere (`PlayerStateReader`, `ZoneIndex`,
`AttunementService`, `MainWindow`, `StatusWindow`) — correct, and it should stay that way.

**The hazard:** `GetExcelSheet<T>()` is generic, so importing the `Experimental` namespace binds a
*different* type with no error and possibly different column definitions. An IDE auto-import could
introduce it silently. If sheet code ever behaves inexplicably, check the `using` list first.

Do not use `Experimental` without a specific reason and a note here saying why.

## 5. Method note — reflection beats the game for schema questions

The probe lives at `scratchpad/itemdrop/` (temporary, not in the repo). It needs only
`Lumina.Excel.dll` and answers in about a second.

**Reach for this before scheduling anything in-game.** R2 was filed as blocked on R1 purely
because I assumed the schema had to come from the in-game probe's report. It did not — schema is
in the assembly, and only row data needs the client. That mistaken assumption would have parked a
solved question behind an unrelated task.

---

## 6. Offline game-data access — what is actually reachable

Verified 2026-08-26 by opening `sqpack` directly with `new GameData(path)` — **no running game
required**. Read-only, and permitted by the scope rules in CLAUDE.md §2.

```
sqpack exists: True        GameData opened OK
Item         52,801 rows      BNpcName     15,001 rows
ENpcResident 59,851 rows      ENpcBase     59,851 rows
Level        61,346 rows
```

Real data comes back — items resolve to names and ilvls, `BNpcName` to creature names
("sandskin peiste", "basilisk"), `ENpcResident` to NPC names.

### Lists of things: yes, comprehensively

Every one of the 1,198 sheets is readable offline. Items, monsters, NPCs, recipes, gathering
items, zones, quests, emotes, mounts.

### Locations: yes for NPCs, partly for monsters

`Level` is the placement sheet: **`X` `Y` `Z` `Yaw` `Radius` + `Territory` + `Map` + `Object`**.
Joining `Level.Object` → `ENpcResident` gives a named NPC at world coordinates:

```
Gontrant        — New Gridania (terr 132) @ (25.0, -8.0, 108.1)
Mother Miounne  — New Gridania (terr 132) @ (23.8, -8.0, 115.9)
```

**Monsters are the gap.** `Level` covers event NPCs and objects, not roaming `BNpc` spawns —
those live in the territory **LGB** layer files, which Lumina can parse but which are far more
work. The cheap monster-location source remains `MonsterNoteTarget.PlaceNameZone` /
`PlaceNameLocation` (zone-level, Hunting Log only). See §3.

### NPC outfits: yes, as models — item names only sometimes

`ENpcBase` carries a full appearance and wardrobe: `ModelHead/Body/Hands/Legs/Feet/Ears/Neck/
Wrists/LeftRing/RightRing`, `ModelMainHand/OffHand`, a complete `Dye*`/`Dye2*` set, plus
`Race`/`Tribe`/`Gender`/`HairStyle`/`SkinColor` and the rest of the character sliders.

**Equipment lives in TWO places and they LAYER — this is not either/or.**

`ENpcBase` has inline `Model*` columns AND an `NpcEquip` row ref. An earlier draft of this
section called them alternative "storage modes" and picked one by asking whether any inline model
was non-zero. **That is wrong**, and the Apartment Merchant proves it: `ENpcBase 1018091` has
`ModelHead` set inline *and* `NpcEquip = 192` carrying everything else. Choosing inline reported
five empty slots on a fully dressed NPC.

The correct rule, verified against Glamourer:

```
for each slot:  inline Model* if non-zero, else the NpcEquip row's value
                (dyes resolve the same way, independently per slot)
```

**Model → item name MUST be keyed by (slot, model, variant).** Keying on model+variant alone is
a silent correctness bug: model `6/1` is both *Dated Straw Hat* and *Dated Taupe Sheepskin
Jerkin*, and a first-one-wins dictionary reported the NPC wearing a straw hat on its **body**.
Filter candidates by `Item.EquipSlotCategory` — `Head`/`Body`/`Gloves`/`Legs`/`Feet`/`MainHand`
== 1 — before matching.

**Worked example — Apartment Merchant, Lily Hills Apartment Lobby (Lavender Beds, terr 574):**

| Slot | Resolved | Glamourer |
|---|---|---|
| Head | `Unknown (28-90)` | ✅ identical |
| Body | Dated Taupe Sheepskin Jerkin *(dye: Mud Green)* | ✅ |
| Hands | Storm Private's Ringbands | ✅ |
| Legs | Dated Canvas Shepherd's Slops (Brown) | ✅ |
| Feet | Dated Leather Crakows (Green) | ✅ |
| Main | Nothing | ✅ |

Six of six, dye included. Glamourer independently prints `Unknown (28-90)` for the head, so it
resolves NPC-exclusive models the same way and by the same convention.

**`65535` means "explicitly hidden"**, not "empty" — distinct from `0`.

### Live in-game alternative

For an NPC standing in front of the player, the sheet route is unnecessary and sometimes wrong
(instanced or story-swapped appearances). The live actor's `Character` struct carries the same
equipment data for **any** actor, not just the local player — the plugin already reads the player's
via `PlayerStateReader.ReadEquipment`. Not exercised against a non-player actor here.
