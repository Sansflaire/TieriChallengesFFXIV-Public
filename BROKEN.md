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
