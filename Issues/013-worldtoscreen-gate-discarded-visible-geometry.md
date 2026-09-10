# 013 — `WorldToScreen`'s two-argument gate silently discarded visible geometry

**Status:** Fix shipped 0.84.42.3, awaiting in-game confirmation · **Severity:** Feature-breaking
**Versions:** introduced 0.84.42.0 (dig site walls); latent in `AreaOverlay` since it was written

---

## What happened

Test 2's Area Surveillance site is drawn as four gradient walls. Sansflaire reported:

> "If I turn my camera in certain angles, I can see some sort of wall you're drawing, but it's
> invisible/gone most of the time when I look around. It's definitely not drawing in the normal
> world space."

## Root cause

The projection gate was:

```csharp
if (!Plugin.GameGui.WorldToScreen(p0, out var s0)) continue;
```

`Dalamud.xml` is unambiguous about what that returns:

| Overload | Returns |
|---|---|
| `WorldToScreen(pos, out screen)` | true if in front of the camera **AND screenPos is in the viewport** |
| `WorldToScreen(pos, out screen, out inView)` | true if in front of the camera; viewport reported separately |

So the gate conflated *"cannot be projected"* with *"lands off the edge of the screen."* The second is
not a failure at all — ImGui clips to the viewport by itself, and a quad with a corner past the edge
is still perfectly visible.

**Why it looked like a world-space bug.** The player stands *inside* a 70-yalm site, so the two near
walls are enormous on screen and essentially always have at least one corner outside the viewport.
Any quad with one bad corner was skipped, so those walls never drew at all. Only when the camera
happened to frame an entire wall — backing off, or looking along it — did all four corners land
in-viewport and the wall snap into existence. That reads exactly like a projection-space error, which
is what it was mistaken for, but the projection was fine and the *gate* was wrong.

## The second defect, found while fixing the first

The site volume is `SizeY = 40` on purpose: `ChallengeArea.Contains` is a fully 3D test, and a flat
slab would stop containing the player the moment the ground was not level. The wall was drawn over
that same height, centred on the player's feet — so **the gradient's opaque base sat ~20 yalms
underground** and everything visible above the surface was already the faded end.

Two separate heights had been treated as one. The containment volume is sized for the *test*; the
wall is sized for *looking at*. They are now independent, and the drawn height is a tuning slider.

## The third, which the first was hiding

Each row was one quad spanning the full 70-yalm wall. Even with the gate fixed, a quad that crosses
behind the camera has no valid projection and must be dropped whole — so a wall the player stood
beside would still lose entire rows. Walls are now a grid (22 rows × 10 columns): only the narrow
column that actually straddles the camera plane is lost. It also cuts perspective error, since
screen-space interpolation across a very long quad is affine and visibly wrong.

## Fix

- `AreaOverlay.Project(Vector3, out Vector2)` — the one gate, using the three-argument overload and
  failing only for genuinely-behind-the-camera points. `DrawSegment`, `DrawRing` and both
  `DigVolumeRender` methods go through it.
- Walls drawn from the site's ground level upward over `DigTuning.SiteWallHeight`, not over the
  volume's height. The wireframe outlines the *drawn* wall, so there is no second boundary.
- Walls subdivided across their width as well as their height.

## This was latent in the Challenge Creator too

`AreaOverlay.DrawSegment` and `DrawRing` had the same gate from the day they were written, so
**creator wireframes have always vanished when a volume grew large on screen** — which reads as "the
overlay is flaky near big volumes" rather than as a bug, and was never reported. Fixing `Project`
fixes both. A bug whose symptom is "sometimes it doesn't draw" is one people work around instead of
reporting.

## Lessons

1. **A boolean returned by a projection API is not automatically "did this project".** Read what it
   actually promises. Here two useful-but-different questions shared one return value, and the
   overload that separates them existed the whole time.
2. **Check the docs for the overload you are NOT using.** The three-argument form's existence was the
   clue that the two-argument form answers a compound question.
3. **"Visible at some camera angles" is a culling symptom, not a coordinate symptom.** A wrong
   transform is wrong from every angle; intermittent visibility that correlates with how much of the
   object is on screen points at a viewport test.
4. **A volume sized for a hit test is not a volume sized for drawing.** Reusing one number for both
   put the visible part of a gradient underground.
5. **Test large.** Every one of these three defects needs the object to be *big on screen* to appear;
   all three are invisible when the shape comfortably fits in view, which is how it was reasoned
   about in code.

## Related

- [012](012-invented-sentinel-crashed-the-game.md) — the other case where an API's signature was
  read as documentation of its behaviour and was not.
