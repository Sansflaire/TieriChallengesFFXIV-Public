# 010 — The sync's circuit breaker made its pruner delete challenges the player already had

**Status:** fixed in 0.84.38.1
**Keywords:** sync, prune, PruneOrphans, keep set, circuit breaker, MaxConsecutiveFailures,
MaxFilesPerSync, early exit, partial run, data loss, official catalogue

---

## Symptom

None reported — found by review. A sync that stopped early would silently **delete cached official
challenges that were still perfectly valid and still published**, removing them from the player's
list until some later sync happened to run all the way through.

## Root cause

Two individually-correct safety mechanisms, composed without noticing what the second assumed about
the first.

`RunAsync` walks the master list downloading each challenge file, and can stop early for two good
reasons — the 200-file cap, and the 5-consecutive-failure circuit breaker required by the
cross-plugin bounded-sweep rule. Afterwards it calls:

```csharp
int removed = _catalog.PruneOrphans(keep);
```

`PruneOrphans` deletes every cached `*.json` whose id is **not** in `keep`. That is correct only if
`keep` means "everything the master list vouches for". It did not. `keep.Add(entry.Id)` sat inside
the loop, *after* both `break` statements:

```csharp
if (++processed > MaxFilesPerSync)             { ...; break; }
if (consecutiveFailures >= MaxConsecutiveFailures) { ...; break; }
keep.Add(entry.Id);
```

So `keep` actually meant "everything this particular run got as far as looking at". Break out five
entries in, and every remaining entry looked like an orphan — a challenge the repo had withdrawn —
and its good cached file was deleted.

A flaky connection was enough. The breaker is what makes the failure likely rather than rare: five
consecutive download failures is exactly what a brief network drop looks like, and the guard that
correctly stops hammering a dead endpoint is the same guard that truncated the keep set.

## Fix

`keep` is built from the whole master list up front, before the loop, independent of what the run
manages to process. Membership of the master list is what makes a file worth keeping; how far this
run got is irrelevant to that question.

## Lessons

- **Adding an early exit changes the meaning of every set the loop was accumulating.** The breaker
  was added later, correctly, to satisfy a rule about not hammering a broken sink. It was not
  obvious that it also silently redefined `keep` from "published" to "seen this run" — and the
  variable's name still says the former.
- **Build the set of things to KEEP from the authority, not from the work.** Anything derived from
  loop progress is a record of what happened, not of what is true. A destructive operation must be
  driven by the latter.
- **Deletion needs a completeness precondition, and it should be stated where the deletion is.**
  `PruneOrphans(keep)` reads as safe at the call site; nothing there says "only correct if `keep`
  is complete". A partial-run flag, or simply refusing to prune after an early exit, would also
  have worked.
- Generalises to any reconcile-and-delete loop: mark-and-sweep, cache eviction, orphan cleanup. If
  the mark phase can be cut short, the sweep must not run — or must run against the manifest rather
  than the marks.
