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
