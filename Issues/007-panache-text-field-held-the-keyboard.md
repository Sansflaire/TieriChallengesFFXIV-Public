# 007 — A Panache text field could not be typed into, then would not give the keyboard back

**Status:** fixed across 0.83.37.1 and 0.83.37.2 (shipped broken in 0.83.37.0)
**Keywords:** search, text input, keyboard, PumpKeyboard, WantTextInput, WantCaptureKeyboard,
focus, InteractionManager, ClearFocus, PUI.TextInput, escape

---

## Symptom, in two stages

**Stage one (0.83.37.0).** The new search box could be clicked into and drew a caret, but typing
did nothing to it — the keystrokes went to the game instead, moving the character and opening
chat.

**Stage two (0.83.37.1).** Fixing that introduced a worse fault, caught by Trist before it was
felt: once a field had been focused, clicking away out into the world did not release the
keyboard. The plugin went on claiming it every frame, with the window still open and no field
visibly focused, leaving the game deaf to the keyboard with no way back short of closing the
window.

## Root cause

`PUI.TextInput` is a **custom-drawn widget, not an `ImGui.InputText`**. ImGui therefore never
learns that a text field has focus, never raises `io.WantTextInput` or `io.WantCaptureKeyboard`,
and Dalamud — which decides whether to swallow keys by reading exactly those flags — goes on
forwarding every keystroke to the game.

`PUI.PumpKeyboard()` is the missing half. It sets both flags and routes
`io.InputQueueCharacters` into the focused node, and its own remarks say it must be called once
per frame from inside the window's `Begin`/`End` block. It was never called.

The second fault is the mirror image. Having claimed the keyboard, something has to give it back.
`InteractionManager` does clear focus on a click no node claimed — but only for clicks the
**surface** sees, and this plugin gates those on `ImGui.IsWindowHovered`. A click anywhere else
never reaches the surface, so focus survived indefinitely.

## Fix

- `PUI.PumpKeyboard()` in `MainWindow.DrawWindow`, immediately after the surface render, inside
  the `Begin`/`End` block. It no-ops when nothing holds focus, so it is unconditional.
- A click this window did **not** receive clears focus. A modal counts as "away": it swallows the
  click before the surface sees it, and its own text fields need the keyboard.
- A closed window never holds focus. The keyboard is already back when hidden — `PumpKeyboard`
  stops running — but the focus itself would survive and re-claim it on reopen.
- **Escape releases everything**, plugin-wide, from one handler (`Plugin.HandleEscape`). Trist's
  standing rule, raised in response to this bug.

## Lessons

- **A custom-drawn text field is a contract with two halves, and shipping one half is worse than
  shipping neither.** Claim the keyboard without releasing it and the failure is not "my search
  box doesn't work" but "my game doesn't respond", which the player has no way to connect to the
  plugin.
- **Read the component's own remarks before using it.** `PumpKeyboard`'s doc comment states the
  requirement, the call site, and the consequence of skipping it. All three were sitting in the
  file the whole time; the box was wired up from its signature alone.
- **Input capture needs a global release, not a per-surface one.** Every window that ever claims
  input is a window that can strand it. One handler that runs before anything draws is the only
  shape that a future surface cannot silently opt out of.
- **An escape hatch must not be able to destroy anything.** Escape is pressed constantly in FFXIV,
  so the release path is deliberately limited to input claims and transient overlays — never a
  running race, a typed search term, or unsaved authoring state.
