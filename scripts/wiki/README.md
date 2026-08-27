# scripts/wiki — Final Fantasy Wiki enemy sweep

Fills the two gaps the game files cannot: **which monsters exist with what stats**, and
**which instance each one lives in**.

```
fetch.py   ->  scripts/wiki/cache/*.json        23 API requests, resumable
parse.py   ->  data/curated/monsters.json       3,504 entries
           ->  data/curated/duties.wiki.json      207 entries
                 |
                 +--> scripts/gen-datasets folds both in during generation
```

Re-run order is `fetch.py` → `parse.py` → `gen-datasets`. Verified idempotent: running the
whole chain twice produces byte-identical datasets (modulo the `generated` timestamp).

---

## Why this source

Mob loot tables and mob locations are **not in the client sheets** — settled exhaustively across
all 1,198 sheet types (TODO Q11/R6). The Hunting Log covers only 259 monsters, 1.8% of the
14,560 named ones. Garland Tools exposes instance fight structure and coffer contents but
**zero creature names**, confirmed across all 368 fetched instances (TODO A6).

The wiki has both, and — critically — it publishes the **`BNpcName` row id** next to every
creature. That is the same key `data/monsters.json` is built on, so the join is exact.

**Verified before anything was built on it:** all 5,035 ids appearing in the tables exist in our
dataset, and 98.3% carry a matching name. The 1.7% that differ are wiki display names against
internal names (*"Orthos Bhoot"* vs *"Mhachi bhoot"*), not a broken join.

---

## 23 requests, not 14,000

The wiki has ~9,000 individual enemy pages, but nearly all are redirects:

```
12th Legion Armored Weapon  ->  #REDIRECT [[Final Fantasy XIV enemies/Forgekin#...]]
```

The real tables live on 19 subpages under `Final Fantasy XIV enemies/`. Fetching those gets
every documented enemy at once, so the polite option and the cheap option coincide.

`fetch.py` carries the three bounded-sweep guards required by `devPlugins/CLAUDE.md`: a hard
request cap (200), a consecutive-failure circuit breaker (5), and a resumable disk cache.
Delete `cache/` to force a refresh; anything already cached is skipped.

---

## Four parsing traps, all of which bit

These tables are hostile to naive parsing. Every one of these produced *plausible, wrong* data
rather than an error — which is why each is now a comment in `wikitable.py`.

**1. rowspan is load-bearing.** 3,869 data cells carry one, up to `rowspan="14"`. A mob in five
duties is written **once** with `rowspan="5"` and five spawn rows beneath it. Line-based parsing
reads rows 2–5 as nameless monsters and loses four of the five locations — exactly the data
being collected. The table is expanded into a real grid first, as a browser would.

**2. The header needs the same expansion.** `!colspan="2"|BNpc` over a second header row of
`!Name !Base` is what makes column 2 *"BNpc Name"* and column 3 *"BNpc Base"*.

**3. Nested tables are cell content, never structure.** 525 collapsible level/HP variant tables
sit inside `colspan="2"` cells. Breaking on their `|}` truncated one 303-row table to **13
rows**; across the corpus that was 3,615 rows instead of 5,294. And because the colspan covers
*both* Level and HP, reading either as a scalar gave junk — level `1` / HP `"1"` for a level
20‑24 mob. The nested table is parsed with the same machinery: its column 0 is level, column 1
is HP.

**4. Tables are not identifiable by their text.** 41 tables on the Beastkin page share just
**two** distinct 200-character prefixes, so locating one with `wt.find(table[:200])` returns the
first match every time — silently filing every Beastkin creature under the page's first family
heading. `split_tables_pos` returns real offsets for this reason.

---

## What it produces, and what it does not

`monsters.json` overlay — `wikiName`, `level`, `hp`, `hitbox`, `abilities`, `family`,
`creatureClass`, `zones`, `duties`, `fates`, `quests`, `dungeonEnemy`, `dungeonBoss`.

| | before | after |
|---|---:|---:|
| monsters with a zone | 259 | **755** |
| monsters with a level | 0 | **3,401** |
| monsters with a duty | 0 | **1,628** |
| duties naming their monsters | 0 | **207 of 373** |

**The 166 duties still without a monster list** are mostly Trials (70) and Raids (76), whose
bosses the wiki documents on their own pages rather than in these enemy tables. Dungeons are
nearly complete (18 missing of 103).

**76 duty names could not be matched, and almost none of them are bugs.** They are content
`data/duties.json` deliberately excludes — deep dungeons (Palace of the Dead, Eureka Orthos),
field operations (Eureka, Bozja, Zadnor, Diadem), treasure dungeons (the Aquapolis, Excitatron
6000), Variant/Criterion (Aloalo Island), and removed duties (the Steps of Faith). Widening
that filter is TODO **A12**; every unmatched name is printed by `parse.py` rather than dropped.

**Trust level.** The wiki states plainly on every page that its classifications are "a
combination of in-game sources and fan conjecture". Levels, HP and abilities are editor-recorded,
not extracted from the client. Treat this as good curated data with a named provenance — which is
exactly why it lands in `curated/` and is labelled `CURATED (external, not game files)` in the
Dataset Viewer, rather than being merged in as though it came from sqpack.
