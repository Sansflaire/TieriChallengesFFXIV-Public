# scripts/garland — curated duty data from Garland Tools

The game files carry no unlock quest and no loot list for a duty. Garland Tools does, so
`duties.json` is **generated first, then augmented here**.

```
scripts/gen-datasets   →  data/duties.json      (game files, offline, authoritative)
scripts/garland/sweep  →  garland-instances.json (cache of 368 instance docs)
scripts/garland/merge  →  data/duties.json      (+ curated columns)
```

## ⚠️ Order matters

`gen-datasets` **overwrites** `duties.json`, curated columns included. After every regeneration,
re-run `merge.py`. That is the whole reason TODO **A10** (split generated from curated) exists —
until it is decided, this ordering is load-bearing and easy to forget.

The cache is committed, so `merge.py` alone is enough. `sweep.py` only needs re-running when the
game patches and new duties appear.

## What the sweep does and does not do

Coverage as of 2026-08-26 — **368 of 373 duties**:

| field | filled |
|---|---:|
| `timeLimitMinutes` | 368 |
| `itemsFound` | 352 |
| `fightCount` | 303 |
| `unlockQuest` | 259 |
| `monsters` | **0 — Garland has no mob lists at all** |

The 5 misses are permanent, not transient: *Special Event I* and *II* 404 on Garland, and
*Storm's Crown*, *Storm's Crown (Extreme)* and *Abyssos: The Fifth Circle* return non-JSON.
Re-running will retry them and fail again — don't.

**`monsters` stays `???`.** `fights` gives Boss/MidBoss structure and chest contents but never a
creature name, confirmed across all 368. It needs a different source entirely (TODO **A6**).

## Politeness

`sweep.py` is deliberately slow and bounded — 2 s between requests, one process, sequential, an
identifying User-Agent, a hard cap of 400 and a circuit breaker after 5 consecutive failures. The
cache is written as it goes so an interruption resumes instead of re-fetching from someone else's
free service. Keep all of that if you touch it; see `devPlugins/CLAUDE.md` on bounded sweeps.
