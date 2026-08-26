# BROKEN.md — Post-Mortem Index

**Read this before debugging anything, and before any commit / build / push.**

This file is an **index only**. One row per resolved issue: ID, one-line summary, keywords,
link to the detail file in [`Issues/`](Issues/). Details, attempts, and lessons live there.

**Tense: past.** Everything here is *fixed*. Active unresolved problems live in
[`KNOWN-ISSUES.md`](KNOWN-ISSUES.md).

**Analogy:** the clogged drain that burst a pipe. Fixed now — but never turn on the faucet
while the drain is clogged.

**This file grows, never shrinks.** When an entry's lesson stops being load-bearing, move its
detail file to `Issues/archive/` and move the row to the Archived table — don't delete it.

**Add an entry when** the bug (a) took more than one attempt, (b) could plausibly recur, or
(c) produced a non-obvious lesson. Add it in the *same commit* that ships the fix.

---

## Live lessons — guard still required

| ID | Summary | Keywords | Fixed | Detail |
|----|---------|----------|-------|--------|
| 001 | PowerShell `Get-Content`/`Set-Content` bulk edit silently double-encoded UTF-8 in four .cs files, mangling comments AND UI string literals. Use the `Edit` tool for source rewrites, never a shell text round trip. | powershell, encoding, utf-8, mojibake, bulk edit | 2026-08-22 | [001](Issues/001-powershell-rewrite-mangles-utf8.md) |
| 003 | Zingle sounds played silently while SE_UI worked. `PlaySound` reports success for an out-of-range index AND for a bus-muted sound, so "it didn't fail" proved nothing. ~15 builds. Ship the audio as `.wav` and play via `winmm`; log failure paths above `Debug`; VFXEditor uses NAudio, not the game mixer, so it is no reference for in-game playback. | sound, scd, playsound, soundmanager, bus, zingle, se_ui, autorelease, vfxeditor, winmm, dalamud.log | 2026-08-23 | [003](Issues/003-game-mixer-silences-plugin-played-zingles.md) |
| 002 | Every published challenge was rejected on download. Files were hashed with Windows CRLF but git stores LF, so the served bytes could never match the recorded SHA-256. Hash what will be *served*: normalise to LF before hashing and pin it with `.gitattributes`. Reached a real user. Also documents the 5-minute raw.githubusercontent cache race that masked it. | sha256, hash mismatch, crlf, lf, git autocrlf, raw.githubusercontent, cdn cache, sync | 2026-08-23 | [002](Issues/002-crlf-hash-mismatch-rejected-every-challenge.md) |
| 005 | Every icon rendered as a grey placeholder in the public build: `build-public.ps1` never packaged `PanacheUI\Icons`, and the framework's folder search only ever succeeds via its dev-machine branch. Invisible locally because that branch is checked first — public-preview mode reproduces the public UI but not the public *file layout*. Package assets in the same commit that starts using them, and fail the build when they are absent. Reached real users. | icons, placeholder, build-public, payload, panacheicons, resolveiconsfolder, works-on-my-machine, shadow copy | 2026-08-25 | [005](Issues/005-icons-missing-from-every-public-zip.md) |
| 007 | A Panache text field could not be typed into, then would not release the keyboard. `PUI.TextInput` is custom-drawn, so ImGui never raises `WantTextInput` and Dalamud keeps feeding keys to the game; `PUI.PumpKeyboard()` must be called once per frame inside the window's Begin/End. Having claimed the keyboard, releasing it is mandatory — `InteractionManager` only clears focus on clicks the surface sees, which are gated on the window being hovered. Escape now releases everything plugin-wide from one handler. | search, text input, keyboard, pumpkeyboard, wanttextinput, focus, interactionmanager, escape | 2026-08-26 | [007](Issues/007-panache-text-field-held-the-keyboard.md) |
| 006 | Map pin landed off the edge of the map in Empyreum. `TerritoryType.Map` returns ONE map per territory, and for a housing district that is the ward — the player was in the subdivision, a different Map row with offsets (702, 655) instead of (0, 0). A territory is not 1:1 with a map. Prefer `AgentMap.CurrentMapId`, and capture the map id with the position at authoring time. | map pin, flag marker, setflagmapmarker, agentmap, housing, subdivision, empyreum, territorytype.map, currentmapid, map offset | 2026-08-26 | [006](Issues/006-map-pin-used-wrong-map-of-a-housing-territory.md) |
| 008 | `ConditionEvaluator` returned `false` for "this build cannot judge this condition" — an unknown `ConditionType`, an unmapped `GameStateFlag`, or a swallowed exception — and `Holds` then applied `Negate` to it. A negated condition over any of the three reported SATISFIED, granting a completion nobody earned. "Fail closed" is a property of the whole expression: a safe default stops being safe the moment anything downstream can invert it. Encode "no answer" as `bool?`, not as the falsy value. | condition, negate, fail closed, fail open, abstain, gamestateflag, conditionflag, tri-state, bool? | 2026-08-26 | [008](Issues/008-abstain-collapsed-to-false-then-negated.md) |
| 010 | The sync's consecutive-failure breaker made `PruneOrphans` delete good cached challenges. `keep` was filled inside the download loop, after both `break`s, so it meant "what this run got to" rather than "what the master list vouches for" — and a brief network drop deleted everything past the stopping point. Two individually-correct guards; adding an early exit silently redefined the set the destructive step depended on. Build the keep-set from the authority, never from loop progress. | sync, prune, pruneorphans, keep set, circuit breaker, early exit, partial run, data loss, mark and sweep | 2026-08-26 | [010](Issues/010-circuit-breaker-made-the-pruner-delete-good-data.md) |
| 009 | Live objective progress had three readers and each read only one of the two stores, so each was blind to exactly the case the other covered: the row showed 0 of 4 outside the challenge's zone, and a `SessionOnly` adventure's objective sheet and map pin showed no progress ever. Three identical bugs is a missing abstraction, not three mistakes — `ChallengeTracker.SatisfiedStops` is now the only reader. | progress, objectives, sessiononly, progressstore, stops, satisfiedstops, map pin, two sources of truth | 2026-08-26 | [009](Issues/009-live-progress-had-three-readers-each-wrong.md) |
| 011 | Every public zip shipped all 167 PanacheUI icons (3.9 MB) when the plugin renders 23 (499 KB): `build-public.ps1` packaged from the shared `devPlugins\PanacheUI\Icons` folder instead of the subsetted `$(TargetDir)\Icons` the build produces. Direct sibling of 005, same file, opposite error — and over-inclusion has no symptom, so nothing was ever going to surface it. Package what the build produced; assert the property, not just presence. | icons, panacheicon, subset, build-public, payload, zip size, targetdir, dead weight | 2026-08-26 | [011](Issues/011-release-packaged-from-the-source-folder-not-the-build-output.md) |
| 004 | Spoiler mask leaked the real zone name in both renderers' detail-pane title (two `ZoneName(` call sites missed after adding the masking wrapper `DisplayName`), and housing zones could never unlock at all — FFXIV has no attunement mechanic for a residential ward, so `Telepo.TeleportList` alone can never clear one. Grep for every remaining call site of the thing you just wrapped; add a persisted "physically visited" signal for zone types with no attunement analog. Reached a real user. | spoiler, attunement, telepo, housing, residential, zonename, displayname, visited-territories | 2026-08-24 | [004](Issues/004-spoiler-mask-leaked-and-never-cleared-housing.md) |

---

## Archived — resolved, no further diagnostic value

| ID | Summary | Keywords | Fixed | Detail |
|----|---------|----------|-------|--------|
| _(none yet)_ | | | | |

---

## Also read

Failures that hit *other* plugins in this suite are catalogued in
[`../BROKEN.md`](../BROKEN.md) (cross-plugin) — a green `dotnet build` is **not** proof the
published artifact is installable. Read that file before any release-bound build.
