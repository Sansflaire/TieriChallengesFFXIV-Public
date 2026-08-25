# Temporary icons

Every icon below is a **stand-in**. The bundled PanacheUI set has no glyph for what the slot
actually means, so the nearest sensible shape is in place until the real one is drawn.

They are listed here rather than left as a `// TODO` because a stand-in that reads *almost* right is
the kind of thing that quietly becomes permanent — nobody files a bug about an icon that is merely
slightly wrong.

Verified against `devPlugins/PanacheUI/ICONS.md` and its committed contact sheet on 2026-08-24:
the set genuinely contains no star, no chevron, no warning triangle, and no apparel/mount/pose
glyph. `0049` is a sparkle cluster and `0058` is a star *medal* — neither tiles into a rating row.

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
| Difficulty, filled | `StarFull` | `0024` filled dot in a ring | Filled 5-point star | Reads as a radio button or a bullet, not an earned rating |
| Difficulty, empty | `StarEmpty` | `0037` hollow circle | Hollow 5-point star | Same — and paired with the above it looks like a progress dot row |
| UI Scale menu item | `Scale` | `0025` concentric rings | Magnifier, or arrows expanding a box | Reads as a target; nothing about it suggests size |

## Slots with no icon at all

These render with `Ico.None` — the menu reserves the column so labels stay aligned, but nothing
is drawn.

| Slot | Should be | Impact today |
|------|-----------|--------------|
| Menu bar dropdown indicator | Chevron down | The menu bar has no affordance showing it opens |
| "Needs a newer plugin" banner | Warning triangle with `!` | A bare red sentence. `0046` was rejected — it reads as *forbidden*, not *heads up* |
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
