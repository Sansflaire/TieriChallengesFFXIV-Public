# Temporary icons

Every icon below is a **stand-in**. The bundled PanacheUI set has no glyph for what the slot
actually means, so the nearest sensible shape is in place until the real one is drawn.

They are listed here rather than left as a `// TODO` because a stand-in that reads *almost* right is
the kind of thing that quietly becomes permanent — nobody files a bug about an icon that is merely
slightly wrong.

**Updated 2026-08-25 — the set grew from ~78 to 167 icons and three of the entries below went
away.** Stars, chevrons and warning triangles all exist now:

- **Difficulty meter** — `StarFull`/`StarEmpty` are `0137`/`0141` (`star-solid-1` /
  `star-outline-1`, softly rounded points). Chosen by rendering all three candidate pairs at the
  11px this actually draws at: the sharp pair `0138`/`0142` has an outline that goes faint at that
  size, and `0139`/`0143` reads heavy. The stand-in comment promised the migration would be "two
  numbers", and it was.
- **Menu dropdown indicator** — `0097` `chevron-down`, beside each menu-bar title.
- **"Needs a newer plugin" banner** — `0121` `warning-triangle-1`, a rounded triangle outline with
  an exclamation inside. `0046` was rejected long ago for reading as *forbidden* rather than
  *heads up*; this one does not have that problem.

The text pips in `ChallengeDef.DifficultyMeterFor` are a separate stand-in and are STILL circles —
see the note above. They serve the fallback renderer and the Creator, neither of which can draw a
bitmap, so a star pair landing in the icon set does not retire them. They should track whatever
`StarFull`/`StarEmpty` look like.

**To swap one in: change the single constant in `MainWindow.Ico` and delete its row here.**
Nothing else references these numbers.

**One exception, added 2026-08-25 — the difficulty meter now has a TEXT stand-in as well.**
`FallbackWindow` and the dev-only Challenge Creator cannot draw a bundled bitmap: the fallback
exists precisely for when the icon renderer is unavailable, and the Creator is plain ImGui. Both
call `ChallengeDef.DifficultyMeterFor`, which builds the meter from `●` and `○`.

Those two characters were chosen to match what `StarFull` / `StarEmpty` actually render *today* —
a filled dot in a ring and a hollow circle. They are deliberately **not** `★` / `☆`, which would
make the two renderers disagree about what the same challenge looks like and would promise artwork
that does not exist. **When a real star pair lands, change the two characters in
`DifficultyMeterFor` in the same commit as the two icon numbers below** — otherwise PanacheUI shows
stars and the fallback still shows circles.

---

## In use now

| Slot | `Ico` constant | Using | Should be | Why the stand-in is wrong |
|------|----------------|-------|-----------|---------------------------|
| UI Scale menu item | `Scale` | `0025` concentric rings | Magnifier, or arrows expanding a box | Reads as a target; nothing about it suggests size |

## Slots with no icon at all

These render with `Ico.None` — the menu reserves the column so labels stay aligned, but nothing
is drawn.

| Slot | Should be | Impact today |
|------|-----------|--------------|
| _(none)_ | | |
| "Report a bug…" menu item | Bug | Only unillustrated item in the Help menu |
| `EmoteAtArea` challenge rows | Person in a pose | Challenge rows carry no kind icon yet |
| `MountInArea` challenge rows | Mount | ” |
| `GearInArea` + `FullOutfit` | Shirt / outfit | ” |
| `Manual` challenge rows | Hand / tap | ” |

---

## The one that is not a stand-in

Challenge-kind rows for `EmoteAtArea`, `MountInArea` and `GearInArea` should eventually use **the
game's own icon for the specific emote, mount or item**, not a generic category glyph. The IDs are
already stored on every challenge (`EmoteId`, `MountId`, `GearItemId`, `OutfitId`), so a challenge
saying "ride the Fatter Cat" can show the actual Fatter Cat icon.

That needs a `TexFile` → `SKBitmap` path via Lumina, which is a real piece of work rather than an
icon swap — the generic glyphs above are the interim step, not the destination.
