# 004: Spoiler mask leaked in two places, and could never clear for housing

**Status:** ✅ FIXED
**Date:** 2026-08-24
**Keywords:** spoiler, attunement, telepo, housing, residential, zonename, displayname, teleport-list, visited-territories

## Symptom

A real player (Trist's friend) reported the Zone tab's detail-pane header showing the real zone
name ("Empyreum") in large text, while the master list one column over showed the same zone as
"??? (unexplored)". Separately, the friend reported that after physically walking into a
residential ward, the challenge inside it still read "Explore this zone to reveal this
challenge" — even while standing in it.

## Root Cause

Two independent bugs, both introduced in the same commit that shipped the spoiler system
(0.81.19.0).

**1. Leak.** `ZoneIndex.DisplayName(cfg, territoryId)` was written as the one sanctioned,
spoiler-aware way to print a zone name to a player — but two call sites were missed and kept
calling `ZoneIndex.ZoneName(territoryId)` directly: the Zone-tab detail pane's title in BOTH
`MainWindow.BuildBody` and `FallbackWindow.DrawBody`. Both had already been through one masking
pass (the right-click tooltip and error text) and were believed complete. They were not — nobody
grepped for every remaining `ZoneName(` call site after adding `DisplayName`.

**2. Housing zones could never unlock.** `AttunementService.IsZoneSpoilered` originally treated
"reached" as "appears in `Telepo.TeleportList`" — the same attuned-aetherytes-and-owned-housing
list the in-game Teleport window uses. That is correct for field zones (you attune to an
aetheryte crystal) but wrong for residential wards: FFXIV has no attunement mechanic for a
housing zone at all. Access to a housing ward's Teleport-list entry comes ONLY from owning a
plot, apartment, or FC room there. A player who walks all through a ward without owning
anything there can never appear in `TeleportList` for that territory — meaning the zone would
read "unexplored" forever, even while the player stood inside it, because the game genuinely has
no unlock signal to key off other than ownership.

## Attempts

| Date | Attempt | Result |
|------|---------|--------|
| 2026-08-24 | Added `ZoneIndex.DisplayName` and routed the right-click tooltip + error text through it | Fixed those two call sites, but two more (`ZoneName` in both detail-pane titles) were never found — no systematic sweep was done |
| 2026-08-24 | Grepped the whole `src/` tree for `ZoneIndex.ZoneName(` after the bug report | Found and fixed both remaining leaks in one pass |
| 2026-08-24 | Added `Configuration.VisitedTerritories` (persisted) + `AttunementService.RecordVisit`, called from `ChallengeTracker.OnFrameworkUpdate` on every zone change | Housing zones (and any zone reached without ever attuning nearby) now unlock the instant the player enters them, independent of ownership |

## Resolution / Lesson

**Grep, don't trust memory, when "fixing every call site of X."** After adding a masking
wrapper, search the whole tree for the thing it wraps (`ZoneIndex.ZoneName(` in this case) before
considering the fix complete. "I already fixed the leaky spots" is not verified until the search
comes back empty. `CLAUDE.md`'s Spoilers section now says this explicitly, with the exact grep
to run before adding any new zone-name-printing surface.

**"Reached" cannot be attunement alone when the zone type has no attunement mechanic.** Any
future "has the player unlocked X" check needs to ask, per zone TYPE, what the game's actual
unlock signal is — for housing, that is ownership (`Telepo.TeleportList`) OR having physically
visited (no `Telepo` equivalent exists, hence `VisitedTerritories`). Don't assume one signal
generalizes across every kind of zone in the game.
