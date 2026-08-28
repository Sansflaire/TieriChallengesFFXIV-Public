# 012 — An invented sentinel (`SetupOrnament(-1)`) hard-crashed the game

**Status:** Fixed 2026-08-28 · **Severity:** Critical — took the game down, twice over
**Versions:** introduced 0.84.38.4, fixed 0.84.38.5

---

## What happened

The dev timeline probe gained a "coupled performance": attach a Fashion Accessory model
client-side, play its animation, and pull the model when the game cancels the animation. Removal
was implemented as:

```csharp
chara->OrnamentData.SetupOrnament(-1, 0);   // "detach"
```

Trist pressed the detach button. **The game crashed** — Dalamud caught the process going down and
started a minidump.

## Root cause

`-1` was invented. It was never read out of any source, never observed in a call, never checked
against a sheet. It *looked* like a sentinel because the parameter happens to be signed:

```csharp
public unsafe delegate void SetupOrnament(OrnamentContainer* thisPtr, short ornamentId, uint param);
```

But the parameter being `short` says nothing about what the callee accepts, and **every field the
client actually stores an ornament id in is unsigned**:

| Field | Type |
|---|---|
| `OrnamentContainer.OrnamentId` | `ushort` |
| `Ornament.OrnamentId` | `uint` |
| `CommonSpawnData.OrnamentId` | `ushort` |

So there was never a negative id in the client's model of an ornament. `-1` as a `short` arrives as
`0xFFFF` = 65535 and is used to index a sheet with ~58 rows. The signedness of the parameter was the
*only* evidence for the guess, and it was not evidence at all.

## It crashed a second time, three minutes later, and that one was self-inflicted

Trist relaunched, and crashed again **while loading into his character**, having touched nothing.
The log is unambiguous:

```
15:19:34.___  (rebuild writes TieriChallengesFFXIV.dll to disk)
15:19:35.233  [LocalPlugin] Unloading TieriChallengesFFXIV
15:19:35.250  [AnimProbe] detach (plugin unload): SetupOrnament(-1)     <-- the bad call
15:19:35.282  [LocalPlugin] Finished unloading
15:19:35.286  [LocalPlugin] Loading TieriChallengesFFXIV.dll
15:19:37.872  TieriChallengesFFXIV v0.84.38.5 (the FIXED build) loaded
15:19:37.283  crash
```

**Writing the DLL hot-reloaded the plugin, and the hot-reload ran the OLD assembly's `Dispose`,
which called `SetupOrnament(-1)`.** The fixed build then loaded perfectly — too late, because the
container had already been corrupted by the outgoing one.

So: *rebuilding while Trist was in game is what crashed him.* Dalamud reloads a dev plugin whenever
its DLL changes, which means **`Dispose` runs at a moment chosen by whoever is compiling, not by the
user.** A teardown that touches game memory is therefore strictly more dangerous than a button — the
developer can fire it remotely, during a loading screen, with no warning.

## Why the crash stack does not mention this plugin

```
[9] Client::Game::Character::OrnamentContainer.Update+0x3E
[7] Glamourer.Interop.ScalingService.UpdateOrnamentDetour(OrnamentContainer*)
[3] Penumbra.Interop.Hooks.Animation.SomeParasolAnimation.Detour(DrawObject*, Int32)
[0] ffxiv_dx11.exe+30F52D                                    <-- C0000005

RCX: FFFF      <-- 65535. The -1.
RSI: ...vtbl_Client::System::Resource::Handle::PapLoadTableResourceHandle
```

The write and the fault are **decoupled**. `SetupOrnament(-1)` stored `0xFFFF` into
`OrnamentContainer.OrnamentId` and returned without complaint. The crash happened on a *later tick*,
when the game's own `OrnamentContainer.Update` read that id back and tried to load the corresponding
`.pap`. Penumbra and Glamourer appear only because they hook that path; neither is at fault.

This is the most misleading property of the whole bug: **the poison is planted and the process dies
somewhere else, in someone else's code.** Anyone reading only the stack would have filed it against
Glamourer. `RCX: FFFF` is the thread that leads back here.

## The second, worse defect

The button was the visible half. The teardown was routed through `ReleasePerformance`, which called
the same crashing line **unconditionally, even when nothing was running** — and that method was
wired into:

- `Plugin.HandleEscape`, which runs on **every Escape press**
- `Plugin.Dispose`, which runs on **every plugin reload**

Escape is pressed constantly in FFXIV. So the shipped dev build was a landmine: the crash was one
keypress away at all times, entirely independently of the probe window being open. Nobody had hit it
yet only because the detach button got there first.

**A cleanup path that runs on a hot, always-on key must do the least possible work, and must be a
no-op when there is nothing to clean up.** Both properties were missing.

## Why the try/catch did not help

The call was wrapped:

```csharp
try { chara->OrnamentData.SetupOrnament(ornamentId, 0); }
catch (Exception ex) { Note($"{label} THREW: {ex.Message}"); }
```

This is worthless against a bad native call. An access violation inside game code is a
**corrupted-state exception**; .NET Core terminates the process rather than delivering it to managed
code. The `catch` only ever covered the managed side — a null function pointer failing to resolve, a
sheet lookup throwing.

The house rule "wrap the body in try/catch, never crash the game" is about **exceptions escaping into
Dalamud's draw loop**. It does not, and cannot, make an unvalidated native call safe. Believing
otherwise is what made the guess feel affordable.

## Fix

- **The entire ornament attach/detach path is removed**, not repaired. There is no verified way to
  remove an attached model, and attaching one with no way back is a trap — fixing a bad guess with a
  second guess (`0`? `OrnamentId = 0`?) would have been the same mistake again. The probe is now
  read-only about ornaments and says so on screen.
- `ReleasePerformance` → `ReleaseHold`, which touches **nothing native**. It clears a managed bool
  and returns early when that bool is already false. Escape and unload can no longer reach game code
  through this window at all.
- Every remaining native call is gated by a **precondition**, not a catch: `TargetIsSafe` requires
  the ActionTimeline row to exist *and* to have a non-empty `Key`, and the fire buttons are disabled
  when it is false. Hold re-checks it every frame, because the id is editable while Hold is running.
- Pointer-capturing lambdas were removed from the fire path so each guarded pointer is used directly
  in the scope it was checked in.

## Lessons

1. **A parameter's type is not documentation of its domain.** `short` did not mean −1 was legal. The
   authority was the *storage* type everywhere else in the struct family, and it said unsigned.
2. **Never invent a sentinel for a native API.** Find a real caller, observe a real value, or do not
   call it. This is the standing "NEVER ASSUME WHEN YOU MUST KNOW" rule, and the cost of breaking it
   here was Trist's game process.
3. **A C# try/catch does not make a native call safe.** Guard with a precondition. If the argument
   cannot be validated, do not make the call.
4. **Never build an acquire with no verified release.** "Attach a model" is only shippable once
   "remove the model" is known — not guessed. The right response to an unverified teardown is to not
   build the acquire.
5. **Audit what a cleanup path is wired into before writing it.** Routing teardown through Escape
   turned a one-button bug into an any-keypress bug. Cleanup on a hot key must be a cheap, managed,
   early-returning no-op.
6. **`Dispose` must never touch game memory.** Dalamud reloads a dev plugin whenever its DLL
   changes, so unload fires at a moment chosen by whoever is compiling — mid-loading-screen, mid-
   fight, with no user action at all. Unload teardown gets the same "managed state only" bar as the
   Escape handler, for the same reason.
7. **Don't rebuild a dev plugin while Trist is in game unless the unload path is known safe.** The
   rebuild *is* a reload. Check whether the game is up first (`GET /status` on the brain answers it
   in one call) — and if unload only touches managed state, as it now does, this stops mattering.
8. **A decoupled write is the worst kind.** The bad value was stored silently and killed the process
   later inside unrelated plugins' hooks. When a native call takes an id that gets *stored*, the
   absence of an immediate crash proves nothing.

## Related

- [007](007-panache-text-field-held-the-keyboard.md) — the other case where a claim needed a
  matching release, and the release was the half that got missed.
- OPEN_QUESTIONS **Q17** — the question the probe was answering (answered yes; the animation itself
  works and needs no ornament at all, which is what makes the removed attach path unnecessary as
  well as unsafe).
