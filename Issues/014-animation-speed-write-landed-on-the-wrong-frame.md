# 014 — The animation speed write landed a frame early, on the wrong animation

**Status:** Fixed 0.84.42.9, confirmed in game · **Severity:** Feature silently did nothing
**Versions:** introduced 0.84.42.7, fixed 0.84.42.9

---

## What happened

`PropService` gained a playback-speed multiplier for the dig animation, driven through
`ActionTimelineSequencer.SetSlotSpeed`. It shipped, and Sansflaire reported:

> "The speed multiplier did not work. It just ended the animation that played at normal speed in
> half the time (at 2x speed)."

A perfect description of the failure: the *hold scaling* worked (a 4 s dig at 2× ended after 2 s),
and the *speed write* did nothing. Two features on the same slider, one working and one inert.

## Root cause — two, independently sufficient

### 1. The write happened on the wrong frame

```csharp
chara->Timeline.PlayActionTimeline(_timeline, 0);
ApplySpeed(chara);                       // <-- immediately after, same frame
```

`PlayActionTimeline` **requests** an animation; it does not install one synchronously. The timeline
does not appear in `TimelineSequencer.TimelineIds[0]` until a later frame — and this class already
*knew* that, because `Stage.Playing` exists for no other reason than to wait for
`slot0 == _timeline`.

So the speed was set on whatever was in slot 0 at that moment — the **outgoing** animation — and the
install then reset the slot. The code contained its own refutation three lines away.

### 2. The game owns the value anyway

`TimelineContainer` carries a `float OverallSpeed` and a `CalculateAndApplyOverallSpeed()` member
function that the game runs itself. Anything it recomputes per update can revert a single write even
when that write is perfectly timed.

Either cause alone produces exactly the observed symptom.

## Why "it compiles and the API is real" proved nothing

The API was verified properly: read out of the decompiled struct, correct signature, slot bounds
checked by the callee. All true, and all irrelevant — **a correct call at the wrong moment is
indistinguishable from a wrong call.** Verification answered "may I call this?" when the open
question was "when does this take effect?"

The confidence note shipped with 0.84.42.7 did name this exact failure mode ("the slot could be
overwritten by the game each frame, in which case the call succeeds and nothing looks different").
Predicting a failure is not the same as guarding against it.

## Fix

- **Capture the baseline before the play call**, while the values still describe what the character
  was doing rather than what we just did to it.
- **Apply when `slot0 == _timeline`** — the first moment a write can survive — and start the hold
  clock there too, which is also the more honest definition of "how long the animation played".
- **Re-apply every frame** for as long as the dig runs. Repetition defeats both causes without
  having to determine which one fired.
- **Three levers, selectable:** `SetSlotSpeed`, `TimelineSpeeds[0]`, `OverallSpeed`. Which one wins
  is not knowable statically, so the method is a setting, defaulting to all three.
- **A live readout of all three** in the lab. "The write is not landing" and "the game reverts it a
  frame later" look identical from outside; only watching the numbers during a dig separates them.
- Restore and the reset button cover all three, not just the function.

## Narrowed by measurement (2026-09-09)

Confirmed working on "All three", then narrowed the same session: **`SetSlotSpeed` and
`TimelineSpeeds[0]` each work on their own.** `OverallSpeed` is not required.

That matters beyond tidiness — `OverallSpeed` is container-level and scales *everything the
character does*, so the working configuration was also the most invasive one. The default is now
`SetSlotSpeed`: the narrowest lever that works, and the game's own setter, so any bookkeeping it
performs beyond storing the float still happens (the raw array write would skip it).

The restore was tightened at the same time to put back **only the levers actually written**.
Restoring all three meant writing a stale `OverallSpeed` baseline over anything the game had
legitimately changed during the dig — mounting, haste, or its own recalculation.

**This is what the selectable-method scaffolding was for.** Exposing all three and reading them live
turned "which of these three works" from three test cycles into one, and then turned the answer into
a smaller, safer default. Building the switch cost less than one wrong guess would have.

## Lessons

1. **Verifying an API's signature is not verifying its timing.** "Does this function exist and may I
   call it" and "when does calling it take effect" are separate questions, and only the first is
   answerable by reading a struct.
2. **A state machine that waits for a thing is telling you the thing is not immediate.** `Stage.Playing`
   existed precisely because the timeline takes frames to install. Any write that depends on that
   animation belongs after the wait, not before it.
3. **Prefer re-applying to reasoning about who resets what.** When a value is shared with code you do
   not control, one write is a race and a per-frame write is a fact. It cost one call per frame.
4. **When several mechanisms could be responsible, make the choice a setting and show the values.**
   Guessing which of three levers works burns a test cycle per guess; exposing all three settles it
   in one session. Same protocol that resolved the ornament question in [012](012-invented-sentinel-crashed-the-game.md).
5. **A predicted failure mode is a to-do, not a disclaimer.** It was written into the confidence note
   and shipped anyway. If a failure is plausible enough to describe, it is plausible enough to
   defend against or to test for before shipping.

## Related

- [012](012-invented-sentinel-crashed-the-game.md) — same family: an assumption about a native API
  that reading the struct did not actually cover.
- [013](013-worldtoscreen-gate-discarded-visible-geometry.md) — the other case this week where the
  API was called correctly and the *usage* was wrong.
