# data/ — reference datasets for the quest generator

Eight JSON datasets, generated from the game's own files and **intended to be hand-augmented** with
the data the game does not expose.

Generated 2026-08-26. Regenerate with `scripts/gen-datasets/` (see below).

---

## The files

| File | Entries | Size | Complete? |
|---|---:|---:|---|
| `duties.json` | 373 | 375 KB | ⚠️ partial — curated from Garland + wiki |
| `monsters.json` | 14,560 | 3.0 MB | ⚠️ partial — 8,473 curated (2 wikis) |
| `recipes-level-based.json` | 9,577 | 2.5 MB | ✅ complete |
| `gatherables.json` | 4,198 | 503 KB | ⚠️ partial |
| `gear.json` | 28,992 | 7.2 MB | ⚠️ partial |
| `fates.json` | 1,712 | 952 KB | ⚠️ partial — 1,193 curated |
| `npcs.json` | 30,878 | 14.3 MB | ⚠️ partial |
| `places-of-interest.json` | 6,435 | 1.1 MB | ✅ game-derived; descriptions partial |

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

**And "nothing" is a THIRD state, written as a real value.** Most monsters drop nothing, so
`drops = "None"` means *documented, and it drops nothing* — a fact, not a gap. Marking those
`???` would invent thousands of false unknowns and send a generator hunting for data that does
not exist. Three states, all distinct: a value · `"None"` · `"???"`.

---

## What is missing, and why

Current as of 2026-08-27. **"Irreducible"** means no amount of work on the client files will
fix it — the data is server-side or simply absent, and only an external source or live capture
could supply it.

| Dataset | Still missing | Coverage now | Why / status |
|---|---|---|---|
| `monsters` | `drops` | 1,129 listed · 3,172 explicitly **None** · 4,185 `???` | Server-side in the client; the Console Games Wiki supplies it. **Most monsters genuinely drop nothing** and that is recorded as `None`, not `???` |
| `monsters` | `zones` / `mapLocation` | **8,503** / 8,433 of 14,560 | Was 259. Zone + in-game coordinates |
| `monsters` | `level` | **6,384** | |
| `monsters` | `abilities` | 1,196 | Only the Fandom tables carry these, and they are sparsely filled |
| `monsters` | exact spawn coords | n/a | Would need LGB layer-file parsing. **Explicitly out of scope** |
| `duties` | `bosses` / `monsters` | **362** / 194 of 373 | From the Console Games Wiki duty pages, joined on `garlandId`. The 11 without bosses have no boss headings on the wiki |
| `duties` | `unlockQuest` | **309** of 373 | The rest are largely Savage/Extreme cleared via the normal version |
| `duties` | whole content types | 373 rows | Deep dungeons, Eureka/Bozja/Diadem, treasure, Variant/Criterion excluded by the generator filter (**A12**) |
| `fates` | `rewards` / `fateType` | **1,165** / 1,193 of 1,712 | EXP, gil, seals, gemstones, item rewards |
| `fates` | `monsters` / `bosses` | 771 / **445** | |
| `fates` | `chainOrder` | 233 | Only that many FATEs are chained at all; `FATEChain` groups but never sequences |
| `fates` | 206 unmatched | — | Shared name and zone did not resolve it — skipped rather than guessed |
| `places-of-interest` | `description` | 239 of 6,435 | Zone pages are inconsistent about documenting landmarks |
| `places-of-interest` | coordinate conversion **unproven** | all 6,435 | Spot-checked only; `rawX`/`rawY` retained so a fix is free (**A14**) |
| `gatherables` | `isCollectable`, `isTimedNode`, `isLegendaryNode` | 0 of 4,198 | Not expressed in a form this generator reads |
| `gear` | `acquisition` | **12,608** of 28,992 | Composed by joining `duties.itemsFound`, `monsters.drops` and `fates.rewards` against gear names, plus the game-derived `craftable` flag |
| `gear` | `acquisition` for the other 16,384 | — | Not a failed match: these come from relic steps, tomestones, vendors, seasonal events, Gold Saucer and PvP — sources none of our datasets cover |
| `gear` | `expansion` | 0 of 28,992 | `Item` carries no `ExVersion` |
| `npcs` | `level`, `isTargetable` | 0 of 30,878 | **Irreducible.** Neither exists client-side |
| `npcs` | `hairColorName` | 0 | Palette is `chara/xls/charamake/human.cmp`, a raw file with no Excel sheet |

**`recipes-level-based.json` and `places-of-interest.json` are the complete, fully game-derived
files.** They are the two safest to build on today without any external data.

Background and method: [`../research/Game Data Cookbook.md`](../research/Game%20Data%20Cookbook.md).

---

## ⚠️ Regeneration will churn git history

These total ~26 MB. Regenerating rewrites every file, so each regeneration adds ~28 MB of new
blobs to history permanently.

## Curated overlays — `data/curated/` (this is DONE, and it is the pipeline)

Anything the game files cannot supply lives in an **overlay**, joined by id:

```
data/curated/duties.json        ← Garland Tools    (unlockQuest, itemsFound, fights, coffers)
data/curated/duties.wiki.json   ← Final Fantasy Wiki (monsters)
data/curated/monsters.json      ← Final Fantasy Wiki (level, zones, duties, abilities, …)
data/curated/monsters.boss.json ← Final Fantasy Wiki (isBoss, bossKind)
data/curated/monsters.cgw.json  ← Console Games Wiki (DROPS, locations, aggression, hunt rank)
data/curated/fates.cgw.json     ← Console Games Wiki (boss, enemies, rewards, chain order)
data/curated/fates.wiki.json    ← Final Fantasy Wiki (zone, coords, type, spawn conditions)
data/curated/duties.zcgw.json   ← Console Games Wiki (bosses, objectives, entrance, unlock)
```

**An overlay is an INPUT to generation, not a patch applied afterwards.** The generator reads
`curated/` while building, so regenerating is idempotent and lossless no matter how often it
runs. The earlier design patched the finished file, which meant the next regeneration silently
destroyed every curated value — the failure this layout exists to prevent (TODO A10, resolved).

**The generator never writes to `curated/`.** Those files are hand- or script-owned and
read-only from the generator's side.

**One file per source.** A dataset may carry several overlays: the bare `<name>.json` is applied
first, then any `<name>.<source>.json` alphabetically. That is why the Garland and wiki sweeps
can each re-run without either destroying the other's work. Each records its own `source`
string, and the generated header lists them all in `curatedSource`.

Provenance is carried through to the header (`curatedFields`, `curatedSource`,
`curatedEntryCount`) and the in-game Dataset Viewer prints a **`CURATED (external, not game
files — …)`** banner above the grid. Curated data is treated as authoritative *and* labelled,
so it is never reviewed as though it had come out of sqpack.

Pipelines: [`scripts/garland/`](../scripts/garland/README.md) ·
[`scripts/wiki/`](../scripts/wiki/README.md) · [`scripts/cgw/`](../scripts/cgw/README.md)

## Schema 2 — three lossless size reductions

Files are written **slim**. Nothing is lost; the in-game **Dataset Viewer reverses all three**, so
what you see there is real field names and `???` columns exactly as if they were stored per row.

1. **Always-`???` fields are stripped from entries** and named once in `omittedAlwaysUnknown`.
   Storing the same `"???"` 30,000 times carries nothing the header does not already carry.
   A field that is `???` only *sometimes* keeps its literal `"???"` per row — there the value is
   real information.
2. **Keys are aliased** (`"a"`, `"b"`, …) with a `fieldAliases` legend. Long descriptive names are
   worth having once, not once per row.
3. **Constant fields are hoisted** into `omittedConstant`. A field carrying the *same* value on
   every entry is stored once — always-`???` is just the case where that value is `???`. This
   alone took `recipes-level-based.json` from 4.5 MB to 2.5 MB, because `unlockType`,
   `unlockBook` and `unlockNote` are identical on all 9,577 rows.
4. **No indentation.**

Result: **67 MB → 26 MB**, with `monsters.json` going 3.5 MB → 767 KB.

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

## Column groups

A dataset may declare `columnGroups`, letting one filter span several columns. `recipes-level-based`
declares:

```json
"columnGroups": { "ingredient (any)": ["ingredient1", … , "ingredient8"] }
```

In the viewer that appears as its own filter target. **INCLUDE** matches if *any* slot contains the
text; **EXCLUDE** requires that *none* does — the only reading that makes "hide recipes using Fire
Shard" work when the shard could be in any of the eight slots.

Ingredients are flat `ingredient1…8` columns rather than a nested array: 8 is the measured maximum
across all recipes, not an assumption from the sheet's padded fixed-width array.

## Regenerating

```bash
cd scripts/gen-datasets && dotnet run
```

Needs `Lumina.dll` + `Lumina.Excel.dll` from `addon/Hooks/dev/`, and a game install at the
default path. Writes straight into this folder. Takes a few seconds.

**Re-run after every game patch** — item ids, recipes and duties all shift.
