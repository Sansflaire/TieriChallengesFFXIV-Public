# 017 — Three independent faults each blanked the D3D11 city, and a fourth wasted two test rounds

**Status:** ✅ **CONFIRMED WORKING 0.84.54.14** — real per-pixel occlusion, characters included
**Severity:** Feature-breaking · **Versions:** introduced 0.84.54.9; never worked until 0.84.54.14

---

## What happened

Test City's D3D11 depth renderer shipped at 0.84.54.9 and drew nothing at all. The panel reported
`ACTIVE — real geometry, depth-tested per pixel` in green the whole time.

**Three independent defects**, any one of which alone produces an identical blank screen, plus a
testing hazard that invalidated two rounds of live results. That combination is what made this
expensive: fixing one real fault and seeing no change looks exactly like having fixed nothing, and
invites reverting a correct fix.

| # | Fault | Fixed |
|---|---|---|
| 1 | `RenderTargetWriteMask` was `None` — colour writes disabled | 0.84.54.11 |
| 2 | HLSL packed the cbuffer matrix column-major — `transpose(M)·v` | 0.84.54.11 |
| 3 | The projection matrix had **no depth column** — `clip.z == 0` everywhere | 0.84.54.14 |
| — | An unbuilt city looks identical to a broken renderer | 0.84.54.14 (loud banner) |

## Root cause 1 — `RenderTargetWriteMask` was `None`

```csharp
_blendOpaque = _device.CreateBlendState(new BlendDescription
{
    AlphaToCoverageEnable  = false,
    IndependentBlendEnable = false,
});
```

`RenderTarget[0]` is never assigned. Vortice's `BlendDescription` has **no parameterless
constructor**, so a C# object initializer zero-initializes the struct — and
`RenderTargetWriteMask = 0` is `ColorWriteEnable.None`. The pipeline was configured not to write a
single colour channel.

Measured against the installed assembly rather than reasoned about, by running the exact initializer:

```
TestCity style          RT0.WriteMask = None (raw 0)
new BlendDescription()  RT0.WriteMask = None (raw 0)
-- ctors --  (Blend, Blend) / (Blend, Blend, Blend, Blend)   <- no parameterless ctor
Assembly 3.8.2.0 / 3.8.2+6f9ca59c…     ColorWriteEnable.All = 15
```

**Zero is a legal value, so nothing complains.** No exception, no debug-layer warning we would see,
no failed `CreateBlendState`. Every draw call "succeeds" and produces nothing. The working sibling,
FFXIV-TV's `CopyBlitRenderer`, assigns `RenderTarget[0]` explicitly — which is not style, it is the
only thing standing between it and this bug.

The comment above the code said *"Opaque… with blending OFF"*, and that intent was right. **Disabling
blending and disabling colour writes are different settings**, and only the first was ever wanted.

## Root cause 2 — the matrix was transposed by HLSL's default packing

```hlsl
float4x4 ViewProj;                              // unqualified
o.pos = mul(float4(input.pos, 1.0f), ViewProj); // row-vector product
```

HLSL packs a cbuffer matrix **column-major** unless told otherwise. .NET's `Matrix4x4` is laid out
row by row. So the shader read our row *i* as its column *i*, and computed `transpose(M) · v`
instead of `v · M`.

Settled by disassembling this exact source through the exact `Compiler.Compile` overload the plugin
uses, rather than by argument:

| As shipped (unqualified) | With `row_major` |
|---|---|
| `dp4 r1.x, r0, cb0[0]` | `mul r0, v0.yyyy, cb0[1]` |
| `dp4 r1.y, r0, cb0[1]` | `mad r0, v0.xxxx, cb0[0], r0` |
| `dp4 r1.z, r0, cb0[2]` | `mad r0, v0.zzzz, cb0[2], r0` |
| `dp4 r1.w, r0, cb0[3]` | |

Four `dp4`s against consecutive registers means `result.j = dot(v, cb0[j])`. Since the upload is
row-major, `cb0[j]` **is row j** — a column-vector product, i.e. the transpose. `row_major` emits the
mul/mad chain, which is the row-vector product FFXIV's matrices are built for.

This also took the occlusion down with it: `clip.w` is garbage under the wrong product, so
`clip.z / clip.w` was never comparable to the captured depth buffer. Had only the write mask been
fixed, the city would have drawn as unrecognisable smears and the depth test would have looked
broken too — three symptoms, one cause.

## Root cause 3 — the projection matrix had no depth column

Found by the one-frame trace, after the first two fixes left the screen still blank:

```
viewProj row0    -0.2657     0.5572     0.0000     0.9539
         row1     0.0000     2.3655     0.0000    -0.2335
         row2     1.3424     0.1103     0.0000     0.1888
         row3  -195.6722    39.8011     0.0000    10.1714
clip          (-0.00,       0.14,       0.0000,    7.586)
```

The **entire third column is zero.** `clip.z` is `dot(position, third column)`, so `clip.z == 0` for
every vertex in the world — no camera angle helps. Under reverse-Z, 0 is the far plane, so this
fails **both** our own `Greater` test against a buffer cleared to 0 **and** the scene-depth
comparison. Either alone draws nothing.

### FFXIV's projection is INFINITE-FAR REVERSE-Z, so `clip.z` is a constant and the depth lives in `w`

This is the part worth remembering, and it is not what "a matrix with a depth column" intuitively
suggests. The candidate dump from the working frame:

```
Zcol=(0.0000, 0.0000, 0.0000, 0.1000)  Control.ViewProjectionMatrix        <- works
Zcol=(0.0000, 0.0000, 0.0000, 0.0000)  view x RenderCamera.ProjectionMatrix2
Zcol=(0.0000, 0.0000, 0.0000, 0.0000)  view x RenderCamera.ProjectionMatrix
clip (0.00, 0.02, 0.1000, 5.209)  ->  ndc z = 0.0192 = 0.1 / 5.209
```

**The working matrix's Z column is also three zeros.** The only difference is `M43 = 0.1`. So
`clip.z` is a *constant* — the near plane — and every bit of depth information is carried by
`clip.w`, which is the view distance:

```
depth = clip.z / clip.w = 0.1 / distance      // 1.0 at the near plane, -> 0 at infinity
```

That is exactly a standard infinite far-plane reverse-Z projection, and it **independently confirms
the reverse-Z convention** the rest of this feature was built on. It also explains the other two
candidates: their `M43` is 0 as well, so `clip.z` is identically zero and no depth exists at all.

**Consequence for any future check: `M43` is the load-bearing element.** A "does this matrix have
depth" test that inspects only `M13`/`M23`/`M33` — which is the intuitive way to write it — rejects
the one matrix that works. `HasDepth` sums all four components of the column for this reason.

**What made it nearly invisible is that everything else was right, and provably so.** The same trace
projected the player's chest to `px(1280.0, 706.3)`, and Dalamud's `WorldToScreen` — the projector
the working painted renderer uses — gave `px(1280.0, 706.3)`. Pixel-exact, with a sane `w = 7.586`.
So X, Y and W were perfect, `row_major` and the multiply order were both confirmed correct, and the
city could never have *looked* wrong. Total absence was the only symptom the fault could produce.

`Render.Camera` holds **two** projection matrices, read from the struct definition rather than
inferred from names: `ProjectionMatrix2` at **+0x50** and `ProjectionMatrix` at **+0x1A0**. The
feature shipped on +0x1A0, the one without depth. A third candidate,
**`Control.ViewProjectionMatrix` (+0x76B0)**, is what FFXIV-TV's `CopyBlitRenderer` uses for this
exact comparison against this exact captured depth buffer — and **it is the one that works**,
confirmed live 2026-09-12. `CityMatrixSource.Auto` prefers it and skips any candidate whose third
column is zero; that test is arithmetic, not a heuristic, so it cannot discard a working matrix.

## The fourth thing: an unbuilt city is indistinguishable from a broken renderer

Two rounds of live testing returned confident negatives that meant nothing, because `Build City` had
not been pressed. `DrawWorld` returns on its first line when `!IsBuilt`, so no probe, toggle or trace
executes at all — and "nothing on screen" is exactly what a broken renderer looks like.

It compounds: **the city is in-memory by design, so every plugin reload wipes it — and every rebuild
is a plugin reload.** Shipping three diagnostic builds in a row therefore created three chances to
test an unbuilt city. The build state also lived on a different tab from the diagnostics, so every
switch could be flipped without ever seeing it.

Fixed by making the banner loud, saying *why* it is unbuilt, and repeating it inside the Diagnostics
block. The deeper rule is below.

## Why it survived a release: the panel said ACTIVE

`TestCityService.UsingDepthRenderer` was set true whenever `TestCityD3D.Render` returned true, and
`Render` returns true having merely *submitted* a draw. Nothing on that path inspects the finished
surface. The panel then rendered that as a green `ACTIVE — real geometry, depth-tested per pixel`.

Every word of that line was a claim about the screen, sourced from a CPU-side success flag. It is
[§4A](../CLAUDE.md) pre-flight question 2 — *how would I know if this silently did nothing?* — failing
in the field: the failure had no symptom the diagnostics could express, because the diagnostics were
written to confirm the happy path rather than to distinguish it from the blank one.

`Render` also returned `true` on the zero-vertex early path while resetting only `VerticesLastFrame`,
so `TrianglesLastFrame` could survive a frame that drew nothing.

## The fix

1. `RenderTarget[0]` assigned explicitly, `RenderTargetWriteMask = ColorWriteEnable.All`,
   `BlendEnable = false`, One/Zero. The pass stays opaque and order-independent as designed.
2. `row_major float4x4 ViewProj;` in the cbuffer declaration. **No CPU transpose** — the upload stays
   `ViewProj = viewProj` and the multiply order stays `view × proj`. Both together would be the same
   bug spelled twice.
3. Telemetry that states what was measured: `TestCityD3D.DrawSubmitted`,
   `TestCityService.DepthSubmitted`, all per-frame counters reset before any early return, and a panel
   that says `SUBMITTED — geometry handed to the GPU this frame` with an explicit note that nothing
   here reads the finished surface.

## Lessons from the third fault and the testing hazard

- **An unbuilt experiment is indistinguishable from a broken one, and the tester will not think of
  it.** Any feature with an explicit build/arm step must say so loudly *at the place where results
  are read*, not on another tab. Two rounds of live testing produced confident negatives that meant
  nothing, and one of them nearly caused a correct fix to be reverted.
- **Shipping a diagnostic build destroys the state under test.** The city is in-memory by design, so
  every rebuild wipes it. Three diagnostic builds in a row created three chances to test nothing.
  When someone is mid-test, *do not rebuild* — and prefer diagnostics that can be re-run without one
  (a live selector beats a recompiled constant).
- **A probe must carry its own control.** The first presentation probe drew only the thing under
  test, so "no magenta" meant either "the image did not present" or "this code never ran" — opposite
  investigations. The second drew a non-textured control beside it and was decisive in one look.
- **Prefer a second opinion that is already known to work.** The single most valuable diagnostic was
  free: projecting one world point with our matrix and with Dalamud's `WorldToScreen`, which the
  working painted renderer already uses. It exonerated X/Y/W instantly and pointed straight at Z.
- **Read the struct, not the name.** `Render.Camera` has two projection matrices and neither name
  says which carries depth; the answer came from the field offsets plus a live dump, and the matrix
  that actually works belongs to a third type entirely.

## Lessons

- **A zero-initialized graphics descriptor is not a defaults struct.** D3D11 descriptors are full of
  fields whose zero value is a legal, silent, catastrophic setting — `ColorWriteEnable.None` here,
  and `CullMode`/`ComparisonFunction`/`Filter` have the same shape. An object initializer that sets
  *some* fields reads like it accepted defaults for the rest. It did not; it set them all to zero.
  Check whether the type has a parameterless constructor before trusting one.
- **An HLSL cbuffer matrix must be declared `row_major` when the CPU side is .NET `Matrix4x4`.**
  The default is column-major and the mismatch is silent.
- **Two independent blockers with one symptom will defeat a serial bug hunt.** Neither fix alone
  changes anything visible. When a feature has never worked once, do not assume a single cause, and
  do not revert a fix because the screen is still blank.
- **Verify against the artifact, not the reasoning.** Both root causes were promoted from "plausible"
  to "certain" in about ten minutes by running the real initializer against the installed
  `Vortice.Direct3D11 3.8.2` and disassembling the real shader through the real compiler. That
  replaced an entire planned diagnostic ladder — magenta clear probe, camera-independent clip-space
  triangle, transpose A/B toggle, bounded staging readback — none of which had to be built.
  Same principle as grepping the built DLL instead of the source.
- **A status line may only claim what it measured.** "Submitted" and "visible" are different facts and
  the gap between them is where this lived. Any future in-world renderer here inherits the rule.

## Not changed, and deliberately

- **Multiplication order and matrix source.** `Matrix4x4.Multiply(view, proj)` with
  `mul(float4(pos,1), M)` under `row_major` is the row-vector convention, and matches FFXIV-TV's
  confirmed-working renderer. A speculative order swap can look plausible at one camera and fail
  everywhere else. If anchors turn out to drift while orbiting, the next step is to switch the
  *source* to `Control.ViewProjectionMatrix` — not to reverse the product.
- **The premultiplied-alpha return.** The PS emits `float4(col.rgb * col.a, col.a)` and ImGui then
  blits the surface. If Dalamud's backend blends straight-alpha, alpha is applied twice — which would
  dim the grid (0.55) and checker (0.16) but leave walls (1.0) untouched. Unread, therefore
  unchanged. Test: the opacity slider at 0.5 should look half, not a quarter.
- **Full graphics-state save/restore.** `Draw` restores RTV/DSV only, not blend/depth/raster/viewport/
  IA/shaders. Low risk — Dalamud's ImGui backend sets its own state and the game's frame is already
  rendered — but real. Address only if other drawing starts misbehaving.
