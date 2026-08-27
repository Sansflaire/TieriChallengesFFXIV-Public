# data/ — reference datasets for the quest generator

Six JSON datasets, generated from the game's own files and **intended to be hand-augmented** with
the data the game does not expose.

Generated 2026-08-26. Regenerate with `scripts/gen-datasets/` (see below).

---

## The files

| File | Entries | Size | Complete? |
|---|---:|---:|---|
| `duties.json` | 373 | 232 KB | ⚠️ partial |
| `monsters.json` | 14,560 | 3.5 MB | 🔴 **needs verification throughout** |
| `recipes-level-based.json` | 9,577 | 9.7 MB | ✅ complete |
| `gatherables.json` | 4,198 | 1.7 MB | ⚠️ partial |
| `gear.json` | 28,992 | 17.8 MB | ⚠️ partial |
| `fates.json` | 1,712 | 1.5 MB | ⚠️ partial |

---

## Conventions

Every file has the same envelope:

```json
{
  "schemaVersion": 1,
  "generated": "2026-08-26T…Z",
  "source": "FFXIV sqpack via Lumina…",
  "description": "…",
  "needsVerification": "…or null",
  "unknownFields": ["…"],
  "unknownMarker": "???",
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

**`recipes-level-based.json` is the only complete file.** It is also the only one safe to build
on today without external data.

Background and method: [`../research/Game Data Cookbook.md`](../research/Game%20Data%20Cookbook.md).

---

## ⚠️ Regeneration will churn git history

These total ~34 MB. Regenerating rewrites every file, so each regeneration adds ~34 MB of new
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

JSON is written **indented on purpose**: these are meant to be opened and edited by hand.
Compact output would roughly halve the size and make that far harder.

---

## Regenerating

```bash
cd scripts/gen-datasets && dotnet run
```

Needs `Lumina.dll` + `Lumina.Excel.dll` from `addon/Hooks/dev/`, and a game install at the
default path. Writes straight into this folder. Takes a few seconds.

**Re-run after every game patch** — item ids, recipes and duties all shift.
