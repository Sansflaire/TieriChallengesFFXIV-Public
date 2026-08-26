# 009 — Live progress had three readers, and each one read the wrong source

**Status:** fixed in 0.84.38.1
**Keywords:** progress, objectives, adventure, SessionOnly, ProgressStore, Stops, SatisfiedStops,
persistence, map pin, objective sheet, 0 of 4, two sources of truth

---

## Symptom

Three separate faults that turned out to be one mistake made three times.

1. **The challenge row read 0 of 4 for an adventure the player had half finished** — and kept
   reading it until they physically walked back into that zone.
2. **A `SessionOnly` adventure's objective sheet read 0 of N for its entire run**, directly beneath
   the gold line promising "All in one login session — progress resets when you log out".
3. **A `SessionOnly` adventure's map pin always pointed at its first stop**, which is the one
   place a pin is useless, and precisely what `NextStopArea` exists to avoid.

## Root cause

Partial progress genuinely lives in two places, and each has a case the other cannot cover:

- `ChallengeTracker._visited` / `._sequence` — the live session state. Filled **only** by
  `Evaluate`, which runs solely inside the challenge's own territory.
- `ProgressStore` — the disk. Written **only** for challenges that persist, i.e. never for a
  `SessionOnly` one.

Each of the three readers picked one source and used it alone, so each was blind to exactly the
case the other source covered. `TryGetProgress` read the session dictionaries, and went blind the
moment the player left the zone. `ObjectiveWindow` and `MapPinService` read the store, and went
blind for the whole class of challenge that never writes to it.

None of the three was obviously wrong at its call site. Reading `Plugin.Progress.Stops(id)` looks
exactly like asking the progress store for progress.

## Fix

One reader: `ChallengeTracker.SatisfiedStops` / `.SatisfiedStopCount`, which answer from the session
state when there is any and fall back to the store otherwise. All three call sites go through it.
`ProgressStore.StopCount` was added alongside, because the row asks once per row per frame and
`Stops` allocates a defensive `HashSet` copy every call.

`MapPinService.Pin`/`AreaOf` now take the tracker rather than the `Configuration` — the config was
only ever threaded in so the method could reach `Plugin.Progress`. `LocationOf` was deleted: dead,
no callers, and it would have carried the same fault to whoever used it next.

## Lessons

- **Two stores with different lifetimes need one reader, written once, before the second caller
  exists.** The split itself was right and is well documented — session-scoped and persistent
  progress genuinely are different things. What was missing was a single function that knew how to
  combine them, so every caller re-derived the answer and each got a different half of it.
- **Three identical bugs is a missing abstraction, not three mistakes.** They were found and fixed
  one at a time in a single afternoon; the third only turned up because the first two had trained
  the eye for `Plugin.Progress.Stops(`. Recorded as a rule in CLAUDE.md so the fourth caller never
  gets written.
- **A UI that contradicts itself on screen is the loudest possible symptom, and it still shipped.**
  The objective sheet printed "progress resets when you log out" immediately above a list showing
  no progress ever. Nobody had opened that window for a session-only adventure.
- **When a helper takes a dependency purely to reach a global, that is a hint the dependency is
  wrong.** `MapPinService` took `Configuration` and used it for nothing but the static hop to
  `Plugin.Progress`; the parameter it actually wanted was the tracker.
