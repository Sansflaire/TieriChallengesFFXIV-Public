# 003 — The game's mixer silences plugin-played zingles, and no parameter reaches it

**Status:** resolved (worked around) · **Fixed:** 2026-08-23 · **Cost:** ~15 builds

## Symptom

`SoundManager.PlaySound` played `sound/system/SE_UI.scd` entries 0–53 perfectly, and played
every `sound/zingle/*.scd` silently. Silent playback reported *success* at every observable
layer: non-null `SoundData`, a real `SoundResourceHandle`, correct `FileSize`, `LoadState 7`,
`IsActive true`, `Volume 1`. Nothing threw. Nothing logged.

## Root cause

Two independent causes wearing the same symptom.

1. **Out-of-range entry.** `SE_UI.scd` holds 54 sounds, so `soundNumber` is 0–53. Entries 55 and
   85 were chosen from VFXEditor's list and do not exist in that index space. **`PlaySound` does
   not reject an out-of-range index** — it returns a pooled `SoundData`, reports active, plays
   nothing.
2. **A closed bus.** The zingle banks load correctly (35 KB / 94 KB / 185 KB, load state 7) and
   are silenced in the mixer. `GetEffectiveVolume(SoundBus.Zingle)` reads `0` against `SE 0.33`.
   `BypassVolumeRules` does not get past it — the bus is applied *after* the category. Writing
   the bus with `SetVolume` does not stick; the engine recomputes effective volume from config.

## Resolution

Cues that need those sounds ship as `.wav` beside the DLL and play through `winmm.PlaySound`,
bypassing the game mixer entirely. Game-bank cues (SE_UI) still route through `SoundManager` and
still obey the in-game sound-effect slider; the shipped files do not, which is the price.

## Lessons

- **A successful `PlaySound` means nothing.** Out-of-range index, empty slot, closed bus and
  wrong-file-loaded all return success. Never infer "it played" from "it did not fail".
- **VFXEditor is not a reference for in-game playback.** `AudioPlayer.CurrentOutput` is
  `NAudio.Wave.WasapiOut` — it decodes the SCD and pushes samples through Windows, never touching
  the game engine. "It works in VFXEditor" says nothing about whether `SoundManager` will play it,
  and a bus-muted sound is exactly where the two diverge. **This was verified 12 builds late.**
- **VFXEditor's `Audio N` labels are not `soundNumber`.** `ScdFile` holds *two* lists: `Audio`
  (`List<ScdAudioEntry>`, what the left panel indexes) and `Sounds` (`List<ScdSoundEntry>`, the
  "Sounds: 54" header). `soundNumber` indexes `Sounds`. The sparse `38, 39, 45, 46…` labels are a
  different table, not empty slots.
- **Log failure paths above `Debug`.** Dalamud filters `Debug` out of `dalamud.log` by default.
  The catch block logged at `Debug` for four builds, so "throws on every call" and "succeeds
  silently" produced *identical* evidence: an empty log. Two wrong diagnoses came from that.
- **A diagnostic whose output you cannot retrieve is not a diagnostic.** `sfx buses` printed to
  chat only and had to be rebuilt to log.
- **Resource handles fill asynchronously.** `size=0 load=3` read immediately after `PlaySound` is
  a mid-load snapshot, not a missing file — the same file read `size=94176 load=7` moments later.
  A "file not found" call was made on that snapshot and was wrong.
- **`autoRelease: false` does not play.** Flipping it to make looping entries stoppable silenced
  every cue; the docs' "stays active so it can be reused" means the handle is prepared, not fired.
  Stop a loop by releasing a tracked handle instead, guarded on `IsActive && SoundNumber == entry`
  so a recycled pool slot is never touched.
- **Verify assumed identifiers before building on them.** "These files use the Zingle bus" came
  from the *folder name* and drove three builds of fixes aimed at that bus. The engine picks the
  bus from the sound data, not the path.

## Tooling that came out of it

`/tchallenges sounds` — dev sound-test panel: a Play per cue showing its real target, an SE_UI
browser clamped to 0–53, bus readout, scan/stop. Built because none of this was falsifiable from
code: every failure mode looks identical from outside, and only listening separates them.
