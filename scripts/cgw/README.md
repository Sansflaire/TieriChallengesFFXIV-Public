# scripts/cgw — FFXIV Console Games Wiki (Gamer Escape) sweep

The only source found for **monster loot**, and the source that finally answered the FATE
gaps. One page per enemy and per FATE, with machine-readable templates.

```
fetch.py         -> cache/_members.json, _pages.json     9,242 enemy pages, 204 requests
fetch_fates.py   -> cache/_fates.json                    1,472 FATE pages,   33 requests
       |
parse.py         -> data/curated/monsters.cgw.json       8,473 entries
parse_fates.py   -> data/curated/fates.cgw.json          1,193 entries
```

**The API is at `/mediawiki/api.php`** — `/api.php` and `/w/api.php` both 404.

Run order: `fetch*.py` → `parse*.py` → `gen-datasets`. The parsers read the generated datasets
to resolve ids, so at least one generation must have happened first.

---

## Why this source

Drop tables are **server-side** — all 1,198 client sheet types were scanned and every `BNpc*`
sheet has zero item references (TODO Q11/R6, settled). The Fandom enemy tables don't carry
loot either. This wiki does:

```
{{NPC infobox | location = South Shroud | coordinates = 17,22 | race = Beastkin
              | clan = Antelope | level = 20-23 | aggression = p1 | patch = 2.0 }}
==Loot==
{{Drops table row|Beast Sinew}}
{{Drops table row|Antelope Shank}}
{{NPC location info|South Shroud| 17,22 |20-23}}
```

It also beats Fandom badly on coverage: **8,473 monsters matched** against 3,516, and location
for **8,392** against 755.

---

## "No drops" is not "unknown"

Most monsters drop nothing, so `???` on them would be a **false unknown** — it would send a
generator looking for data that does not exist. The page distinguishes the cases:

| page has | `drops` becomes | count |
|---|---|---:|
| drop rows | the item list | **1,135** |
| a Loot section, no rows | `None` — documented, drops nothing | **3,182** |
| no loot markup at all | left `???` — genuinely undocumented | 4,185 |

---

## FATEs — all four gaps at once

`{{FATE infobox}}` carries what no client sheet does:

```
| boss = Cuachac        | prev-fate = / next-fate =    <- CHAIN ORDERING
| enemies =             | exp / gil / seals / bicolor gemstone / mettle / item-reward(1-4)
| location / location-x / location-y / type / level / duration
```

`prev-fate`/`next-fate` is the **sequence**. The game's `FATEChain` groups a chain but never
orders it, so this was previously recorded as irreducible — it was irreducible *from the client*.

Coverage of the 1,193 matched: rewards 1,165 · fateType 1,193 · monsters 771 · **bosses 445** ·
chainOrder **233** (only that many FATEs are actually chained).

**206 are skipped, not guessed.** Their name is shared with another FATE and the zone did not
resolve it. 64 exist on the wiki but not in our dataset.

---

## Bosses keep their own column

`fates.bosses` is separate from `fates.monsters`, matching `duties.bosses`. Standing rule: any
dataset holding both enemies and bosses splits them.

---

## Two traps this sweep hit

**1. Infobox fields are not one-per-line.** Some pages write several on a single line:

```
{{FATE infobox| title = Now Fall| location =| location-x =| ...}}
```

A `^\s*\|\s*(key)\s*=\s*(.*)$` regex then captures `"| location-x ="` as the value of
`location` — and that shipped into `fates.json` as `zone = "| location-x ="` before it was
caught. `infobox.py` splits on **depth-0 pipes** instead. Fixing it also raised monster `level`
coverage from 4,753 to 8,175 and `aggression` from 5,243 to 8,134, so the same bug had been
quietly eating fields on thousands of monster pages.

**2. Decode from the template, never from the look of the value.** `aggression = p1` renders as
"Passive". `Template:NPC infobox` does: first character `p` ⇒ Passive, anything else ⇒
Aggressive, and the remaining character is a **rank 1–6**. The odd `r5` is Aggressive by that
same rule, so it is followed exactly rather than special-cased on a guess.

---

## Trust level

Community wiki, editor-maintained. Loot lists are the part most likely to be incomplete rather
than wrong. Everything lands in `data/curated/` and is labelled `CURATED (external, not game
files)` in the Dataset Viewer, so it is never mistaken for sqpack-extracted data.
