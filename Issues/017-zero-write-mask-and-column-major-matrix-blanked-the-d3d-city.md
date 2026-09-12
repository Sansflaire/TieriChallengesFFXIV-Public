# 017 — A zero write mask and a column-major matrix each blanked the D3D11 city, independently

**Status:** Fixed 0.84.54.11, awaiting in-game confirmation · **Severity:** Feature-breaking
**Versions:** introduced 0.84.54.9 (the D3D11 depth renderer's first release); never worked

---

## What happened

Test City's D3D11 depth renderer shipped at 0.84.54.9 and drew nothing at all. The panel reported
`ACTIVE — real geometry, depth-tested per pixel` in green the whole time.

Two **independent** defects, either of which alone produces an identical blank screen. That is why
the fault was hard to reason about from the outside: fixing one and seeing no change looks exactly
like having fixed nothing, and invites reverting a correct fix.

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
