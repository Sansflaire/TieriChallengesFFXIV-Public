# data/ — reference datasets for the quest generator

Six JSON datasets, generated from the game's own files and **intended to be hand-augmented** with
the data the game does not expose.

Generated 2026-08-26. Regenerate with `scripts/gen-datasets/` (see below).

---

## The files

| File | Entries | Size | Complete? |
|---|---:|---:|---|
| `duties.json` | 373 | 107 KB | ⚠️ partial |
| `monsters.json` | 14,560 | 767 KB | 🔴 **needs verification throughout** |
| `recipes-level-based.json` | 9,577 | 4.5 MB | ✅ complete |
| `gatherables.json` | 4,198 | 503 KB | ⚠️ partial |
| `gear.json` | 28,992 | 7.2 MB | ⚠️ partial |
| `fates.json` | 1,712 | 800 KB | ⚠️ partial |
| `npcs.json` | 30,878 | 14.3 MB | ⚠️ partial |

---

## Conventions

Every file has the same envelope:

```json
{
  "schemaVersion": 2,
  "generated": "2026-08-26T…Z",
  "source": "FFXIV sqpack via Lumina…",
  "description": "…",
  "needsVerification": "…or null",
  "unknownFields": ["…"],
  "omittedAlwaysUnknown": ["level","drops","abilities"],
  "unknownMarker": "???",
  "fieldAliases": { "a": "id", "b": "name", "c": "zones" },
  "count": 9577,
  "entries": [ … ]
}
```

- **`"???"`** — this field is **not available from game data**. It is a slot waiting to be filled,
  never a claim that the value is empty or zero.
- **`needsVerification`** — set at the **top of the document** when a gap affects every entry.
  `null` means the file is complete as generated.
- **`unknownFields`** — the exact field paths that are `???`, so nothing has to be inferred by
  eyeballing the data.

**`"???"` and "absent" are different.** `monsters[].drops = "???"` means *we do not know*, not
*this monster drops nothing*. Any consumer must treat `???` as unknown and refuse to reason from
it — that distinction is the entire point of the marker.

---

## What is missing, and why

| Dataset | Missing | Why |
|---|---|---|
| `duties` | `monsters`, `itemsFound` | Not in client data at all |
| `duties` | `unlock` for 754 of 857 | `ContentFinderCondition.UnlockCriteria` is empty for most; only 102 record anything |
| `monsters` | `level`, `drops`, `abilities`, `mapLocation`, `inInstance` | None exist client-side |
| `monsters` | `zones` for ~98% | Only the Hunting Log's 259 mobs have one |
| `gatherables` | `isCollectable`, `isTimedNode`, `isLegendaryNode` | Not expressed in a form this generator reads |
| `gear` | `expansion` | `Item` carries no `ExVersion` |
| `gear` | `acquisition` except crafted | Drop/relic/tome/vendor sources are not in client data |
| `fates` | `monsters`, `monsterAbilities`, all `rewards` | FATE spawn tables and reward tiers are server-side |
| `fates` | chain **ordering** | `FATEChain` groups them; the sequence is not stored |
| `npcs` | `level`, `isTargetable` | Neither exists client-side |
| `npcs` | `hairColorName` | The palette is `chara/xls/charamake/human.cmp`, a raw file with no Excel sheet — only the raw index is available |
| `npcs` | `locations` for some | NPCs with no `Level` row are instanced or cutscene-only |

**`recipes-level-based.json` is the only complete file.** It is also the only one safe to build
on today without external data.

Background and method: [`../research/Game Data Cookbook.md`](../research/Game%20Data%20Cookbook.md).

---

## ⚠️ Regeneration will churn git history

These total ~28 MB. Regenerating rewrites every file, so each regeneration adds ~28 MB of new
blobs to history permanently.

**Recommended before any hand-editing begins: split generated from curated.**

```
data/generated/   ← regenerated freely, never hand-edited
data/curated/     ← small overlay files, hand-maintained, joined by id
```

Otherwise the first regeneration silently overwrites hand-entered work, which is a much worse
outcome than repository size. The overlay is also tiny, reviewable in a diff, and survives a
game patch — the generated half does not.

**Not done yet** — flagged for Trist rather than decided unilaterally, since it changes the
layout he asked for.

## Schema 2 — three lossless size reductions

Files are written **slim**. Nothing is lost; the in-game **Dataset Viewer reverses all three**, so
what you see there is real field names and `???` columns exactly as if they were stored per row.

1. **Always-`???` fields are stripped from entries** and named once in `omittedAlwaysUnknown`.
   Storing the same `"???"` 30,000 times carries nothing the header does not already carry.
   A field that is `???` only *sometimes* keeps its literal `"???"` per row — there the value is
   real information.
2. **Keys are aliased** (`"a"`, `"b"`, …) with a `fieldAliases` legend. Long descriptive names are
   worth having once, not once per row.
3. **No indentation.**

Result: **67 MB → 28 MB**, with `monsters.json` going 3.5 MB → 767 KB.

**Editing by hand?** Use the legend at the top of the file to find the alias for the field you
want. If that proves annoying in practice, the aliasing is one flag in the generator.

---

## `npcs.json` — two conventions worth knowing

- **A slot absent from `equipment` is EMPTY.** All twelve slots serialised for all 30,878 NPCs
  produced a 60 MB file, ~70% of which was the word `"Nothing"`. `???` is never used inside
  `equipment` — absence is the only "nothing here" signal.
- **`"Unknown (28-90)"` means NPC-exclusive gear** with no player-equippable item. That is a
  normal result, not a failure, and `modelId` is retained for exactly those entries because it is
  the only handle on them. Glamourer prints the same string for the same models.

Equipment uses the Glamourer-validated algorithm in
[`../research/Game Data Cookbook.md`](../research/Game%20Data%20Cookbook.md) §5 — inline
`ENpcBase` models layered over `NpcEquip` per slot, with the item lookup keyed by
`(slot, model, variant)`.

## Regenerating

```bash
cd scripts/gen-datasets && dotnet run
```

Needs `Lumina.dll` + `Lumina.Excel.dll` from `addon/Hooks/dev/`, and a game install at the
default path. Writes straight into this folder. Takes a few seconds.

**Re-run after every game patch** — item ids, recipes and duties all shift.
