# TODO — Challenge Tokens & Randomized Quests

**This is the checklist and the single source of STATUS.** Tick items here. The companion docs
hold the reasoning and must not track status themselves, or the three will drift:

- [`Tokens Build Backlog.md`](Tokens%20Build%20Backlog.md) — why/how detail for every build item
- [`Pending Verification.md`](Pending%20Verification.md) — detail + the Completed answer log
- [`Challenge Tokens and Quests.md`](Challenge%20Tokens%20and%20Quests.md) — the design record

Created 2026-08-26. Nothing on this list is built yet.

## The rules (Sansflaire, 2026-08-26 — see `CLAUDE.md` §6A)

1. **This is THE official list.** We work off it. Sansflaire requests something → it goes on with a
   permanent ID. We change something in the project → **its item comes off, in the same commit as
   the change.** Not later, not in a cleanup pass. *(Implemented as "move to Done with the date",
   matching the `OPEN_QUESTIONS.md` convention — off the active list either way.)*
2. **Assume Sansflaire cannot test.** If he asks for the next thing to work on and has not said he is
   at home, offer only a **🤖** item — completable with nobody but Claude. Never propose an
   in-game action, a decision, or visual sign-off unless he says he is available.
3. **Priority order**, applied as a tiebreak chain top-down:
   **(a)** quick/easy/simple → **(b)** unblockers, easiest first → **(c)** non-client/host/web
   (core, data, detection before UI, server, web) → **(d)** everything else.

**IDs are permanent.** Never renumber — other docs and commit messages reference them. Add new
items with the next free number in their block; leave gaps where things are dropped.

**Tags:** 🤖 Claude can do this alone · ⚡ cheap · 🔴 blocks other work · 🔒 needs a secret ·
🙋 **needs Sansflaire** (in-game action, decision, or sign-off)

**Blocked items carry an indented `⛔ Blocked by:` line.** An item with no such line can be
started right now. Update the indented line in the same edit that changes a dependency.

---

## 🔬 Research — find something out

- [ ] **R1** 🙋 🔴 Run `/tchallenges probe` — do gather / craft / buy produce different `ConditionFlag`
      sets? Tool is built and loaded; ~5 min in game.
- [ ] **R3** 🙋 Kill attribution live test — confirm a named mob counts correctly
  - ⛔ Blocked by: **I1** *(no hook exists to test)*
- [ ] **R4** 🙋 Turn-in / vendor detection — is `ItemRemoved` + vendor addon reliable?
- [ ] **R5** 🤖 Garland Tools terms — may we redistribute their data in the Sync repo?
- [ ] **R7** 🙋 **Fandom licensing.** Wiki text is **CC-BY-SA**, and `origin` here is the PUBLIC
      repo — so `data/curated/monsters.json`, `duties.wiki.json` and `scripts/wiki/cache/` are
      already published. Attribution IS recorded (every overlay carries a `source` line, surfaced
      in the header and the viewer's CURATED banner), which likely satisfies BY. **Share-alike is
      the open question**, and it may want a `LICENSE`/attribution note in the repo root. Same
      shape as R5; decide both together. Not a blocker — flagged, not assumed resolved.

## 📋 Review & decide — needs Sansflaire

- [ ] **V1** 🙋 Public repo history — leave the already-pushed anti-cheat §13, or rewrite? *(rec: leave)*
- [ ] **V2** 🙋 🔴 Curated raw-materials list — contents + schema *(Sansflaire owns this)*
- [ ] **V3** 🙋 Quest step structure — what a multi-part quest actually looks like
  - ⛔ Blocked by: **R1** *(step verbs must be things we can actually detect)*
- [ ] **V4** 🙋 🔴 Bracket boundaries — level bands + per-expansion coverage
- [ ] **V5** 🙋 Token values per tier — confirm 10–25 / 50–200 / 500–1500
- [ ] **V6** 🙋 Reroll pricing — or drop rerolls, since they undercut the hardest-bracket rule
- [ ] **V7** 🙋 Claim-transfer threshold — below how many Tokens does a name match merge silently?
- [ ] **V8** 🙋 Skim the backlog for anything discussed that never got captured

## ✅ Actions — not code

- [ ] **A1** 🤖 ⚡🔒 Generate `TOKEN_PEPPER` → back up to (1) private vault repo, (2) local PC beside
      `SansflaireCertificate\`, (3) `SECRETS.local.md` → then `wrangler secret put`.
      **Losing it makes every stored `identityHash` unlookupable forever.**
- [ ] **A2** 🤖 ⚡🔒 Create `ADMIN_KEY` and `LODESTONE_UA`
- [ ] **A3** 🙋 Seed the materials list — one-time dev-side extraction pass
  - ⛔ Blocked by: **V2** *(no schema to extract into)*, **R5** *(redistribution unresolved)*
- [ ] **A5** 🙋 **Fill the last of `data/duties.json`** — `bosses` is 362 of 373 and
      `unlockQuest` 309; the remainder have no boss heading on the wiki, or are Savage/Extreme
      tiers unlocked by clearing the normal version. `monsters` is 194 — the ==Enemies== section
      exists on only 189 duty pages. Low value now; the important columns are filled.

- [ ] **A14** 🙋 **Confirm the map-coordinate conversion in game.** `MarkerToMap` is
      spot-checked (Summerford Farms → 25.2, 16.8 vs a known 25, 17) but not proven across map
      scales. Stand somewhere in `places-of-interest.json` and compare the readout. `rawX`/`rawY`
      are stored so a correction needs no re-derivation.
- [ ] **A12** 🤖 **Widen the duty content-type filter.** `duties.json` holds only Dungeons /
      Trials / Raids / Ultimate / Chaotic (373 rows). The wiki sweep referenced **76 duty names
      we simply do not carry**: deep dungeons (Palace of the Dead, Eureka Orthos), field ops
      (Eureka, Bozja, Zadnor, the Diadem), treasure dungeons (the Aquapolis, Excitatron 6000),
      and Variant/Criterion (Aloalo Island). Each is real challenge surface, and the monster data
      for them is **already sitting in the wiki cache** — widening the filter alone would attach
      it. Cheap, and no new fetching.
- [ ] **A7** 🙋 **Fill `data/gatherables.json`** — `isCollectable`, `isTimedNode`,
      `isLegendaryNode` are ??? for all 4,198 entries.
- [ ] **A8** 🙋 **Fill the rest of `data/gear.json`** — `acquisition` is now **12,608 of 28,992**
      (crafted 6,702 · duty 5,897 · monster drop 53 · FATE 8), composed by joining the datasets
      we already have. The remaining 16,384 need sources we do not cover at all: relic steps,
      tomestone and vendor purchases, seasonal events, Gold Saucer, PvP. `expansion` is still
      ??? for all 28,992 (`Item` carries no `ExVersion`).
- [ ] **A9** 🙋 **Fill the rest of `data/fates.json`** — **largely done.** The Console Games
      Wiki supplied `monsters` (771), `bosses` (445), `rewards` (1,165) and **chain order**
      (233 — only that many FATEs are chained), plus zone/coords/type for 1,193 of 1,712. Those
      three were previously recorded as irreducible; they were irreducible *from the client*.
      Remaining: 206 FATEs whose name is shared and whose zone did not resolve it.
- [ ] **A11** 🙋 **Fill `data/npcs.json`** — `level`, `isTargetable` and `hairColorName` are ???
      for all 30,878 entries. Hair colour needs the `human.cmp` palette decoded (no Excel sheet);
      level and targetability are not in client data at all.

## 🔨 Implement

### Blocking
- [ ] **I48** 🤖 More than ONE authored activity map. `DigTuning` stores a single hand-drawn box
      against a single `BoxTerritory`, so drawing one in a second zone replaces the first —
      `ActivityCatalog.AuthoredMaps` already returns a list and needs no change when the store
      becomes per-territory.
- [ ] **I1** 🤖 ⚡🔴 Kill hook (`ActionEffectHandler.Receive`, DamageMeter pattern) + `Enemy` condition
      type. **Also unblocks the pre-existing 1.0 enemy-challenge milestone — pays for itself twice.**
- [ ] **I2** 🤖 🔴 Materials list format + loader
  - ⛔ Blocked by: **V2**
- [ ] **I3** 🤖 🔴 Quest generator — backward-chaining, exclusion rules, minimum-completion-time
  - ⛔ Blocked by: **I2** *(no input data)*, **R1** *(unknown which verbs are detectable)*, **I1** *(Hunt routes)*

### Content pipeline
- [ ] **I4** 🤖 `active.json` + publisher
  - ⛔ Blocked by: **I3** *(nothing to publish)*
- [ ] **I5** 🤖 Quest archive
  - ⛔ Blocked by: **I4**
- [ ] **I6** 🤖 Bracket definitions
  - ⛔ Blocked by: **V4**
- [ ] **I7** 🤖 Exclusion data — savage/extreme expansion window as **data**, not a constant

### Client — quests
- [ ] **I8** 🤖 Quest definition sync *(extends `ChallengeSyncService`)*
  - ⛔ Blocked by: **I4** *(nothing to fetch)*
- [ ] **I9** 🤖 Quest display UI — tier × route × bracket
  - ⛔ Blocked by: **I8**, **I6**
- [ ] **I10** 🤖 Bracket eligibility from job levels
  - ⛔ Blocked by: **I6**
- [ ] **I11** 🤖 Quest instance state machine
  - ⛔ Blocked by: **I8**
- [ ] **I12** 🤖 Breadcrumb recorder — batched onto completion, never per-step
  - ⛔ Blocked by: **I11**
- [ ] **I13** 🤖 Completion submission — never sends a Token value
  - ⛔ Blocked by: **I11**, **I22**
- [ ] **I14** 🤖 Token balance display — Lifetime vs Balance
  - ⛔ Blocked by: **I22**
- [ ] **I15** 🤖 Token spending UI
  - ⛔ Blocked by: **I35**

### Client — accounts
- [ ] **I16** 🤖 ⚡ Local 128-bit account secret
- [ ] **I17** 🤖 ⚡ Account tier setting — Local / Anonymous / Lodestone. Tier 0 makes **zero** requests
- [ ] **I18** 🤖 ⚡ Obfuscated local Token cache
- [ ] **I19** 🤖 Recovery popup — missing-secret case only, **not** on rename
  - ⛔ Blocked by: **I16**, **I22**
- [ ] **I20** 🤖 Lodestone link flow (client half)
  - ⛔ Blocked by: **I27**
- [ ] **I21** 🤖 Offline queue + retry *(low priority)*
  - ⛔ Blocked by: **I13**

### Server — Cloudflare (nothing exists)
- [ ] **I22** 🤖 Token API Worker — accept / complete / balance / resync
  - ⛔ Blocked by: **I23**
- [ ] **I23** 🤖 ⚡🔴 D1 schema — accounts, append-only ledger, instances, label history, review queue
- [ ] **I24** 🤖 Idempotency — one instance awards Tokens once, ever
  - ⛔ Blocked by: **I23**
- [ ] **I25** 🤖 Server-side Token value lookup
  - ⛔ Blocked by: **I23**
- [ ] **I26** 🤖 Per-row salt + peppered HMAC
  - ⛔ Blocked by: **A1** *(no pepper)*, **I23**
- [ ] **I27** 🤖 Lodestone scraper — one-shot, rate-limited, **never scheduled**
  - ⛔ Blocked by: **A2**, **I23**
- [ ] **I28** 🤖 Materialized leaderboard cache
  - ⛔ Blocked by: **I23**
- [ ] **I29** 🤖 Review queue + admin endpoints *(include `pluginVersion`)*
  - ⛔ Blocked by: **I23**
- [ ] **I30** 🤖 Anomaly detection cron — thresholds live in `SECURITY.local.md`
  - ⛔ Blocked by: **I23**
- [ ] **I31** 🤖 Ban integration — suspend accrual, never delete the ledger
  - ⛔ Blocked by: **I23**

### Web
- [ ] **I32** 🤖 Public leaderboard
  - ⛔ Blocked by: **I28**
- [ ] **I33** 🤖 Admin review view
  - ⛔ Blocked by: **I29**
- [ ] **I34** 🤖 Resync endpoint
  - ⛔ Blocked by: **I23**

### Rewards
- [ ] **I35** 🤖 Purchase / ownership model ← **the actual gap**
  - ⛔ Blocked by: **I23** *(ownership is server-side truth)*
- [ ] **I36** 🤖 Wire up rewards: backgrounds, fanfares, fly-text, toasts, themes
      *(all five already have working systems)*
  - ⛔ Blocked by: **I35**
- [ ] **I37** 🤖 Titles / flair, prestige unlocks *(new build)*
  - ⛔ Blocked by: **I35**

### Dev experiments — Test City
- [ ] **I56** 🙋 **Tune the depth bias now that occlusion works.** Default is `0.00002`. Raise it if
      a building's base speckles against the ground it stands on; lower it if a wall shows through
      something in front of it. Reverse-Z is non-linear, so the right value up close is not the right
      value at 200 yalms — there is no single correct number, which is why it is a knob and not a
      const. Do this in a zone with both near and far buildings.
- [ ] **I55** 🤖 Decide the **premultiplied-alpha** question, if the grid/checker look too faint.
      The PS returns `float4(col.rgb * col.a, col.a)` and ImGui then blits the surface; if Dalamud's
      backend blends straight-alpha, alpha is applied twice — dimming the grid (0.55) and checker
      (0.16) and leaving walls (1.0) untouched. Deliberately unread rather than guessed. Test: the
      opacity slider at 0.5 should look half, not a quarter.
- [ ] **I57** 🤖 Fold the diagnostics down once the feature has been stable for a while. The three
      switches, the matrix selector and the one-frame log dump all earned their place, but a
      permanent debug surface is a maintenance obligation (§6). Keep the matrix selector and the
      NOT-BUILT banner; the probes can go.
- [ ] **I52** 🤖 Draw the **Goal Area** through the D3D11 depth renderer, so the character occludes
      the parts of the ring they stand in front of. Sansflaire's note, 2026-09-12 — flagged as a
      *later* item at the time, not a now item.
      The wall is translucent banded geometry, so it cannot simply join the city's opaque pass: it
      needs either depth-test-on / depth-write-off with back-to-front ordering, or to keep its
      bands and accept per-band sorting. Decide which when the city's own depth path is confirmed
      working — the answer depends on whether the opaque pass turns out to be the right shape to
      extend.
  - ⛔ Blocked by: **the city's depth path drawing at all** — see the `PLAN.md` diagnosis; there is
    no point extending a renderer that currently outputs nothing.

### Cheap wins — no dependencies, any time
*(all done — add new ones here)*

---

## Startable right now — nothing blocks these

### 🤖 Claude can do these alone — the answer to "what's next?" when Sansflaire is away

Listed in **rule 3 priority order**, so the top item is the default recommendation:

| # | Item | Why it ranks here |
|---|---|---|
| 1 | **A13** Fetch the 33 boss duty-article pages | (a) tiny, (b) unblocks the Trial/Raid half of `duties.bosses`, (c) data |
| 1b | **A12** Widen the duty content-type filter | (a) small, (c) data. The monster data for the missing 76 duties is **already in the wiki cache** — no fetching, just a filter |
| 2 | **I7** Exclusion data as data, not a constant | (a) small, (c) core/data |
| 3 | **I16** Local account secret | (a) small, (c) core |
| 4 | **I17** Account tier setting | (a) small, (c) core |
| 5 | **I18** Obfuscated local Token cache | (a) small, (c) core |
| 6 | **I1** Kill hook + `Enemy` condition | (b) **biggest unblocker** — frees R3, I3, and the 1.0 enemy milestone. Harder than the above, so it ranks below them |
| 7 | **I23** D1 schema | (b) unblocks ten server items, but (c) demotes it — it is host work |
| 8 | **A1** Generate + back up `TOKEN_PEPPER` | 🔒 unblocks I26; do before anything needs it |
| 9 | **A2** `ADMIN_KEY`, `LODESTONE_UA` | 🔒 unblocks I27 |
| 10 | **R5** Garland Tools terms | Research; unblocks A3, but the answer may be "ask a human" |

### 🙋 Needs Sansflaire — only offer these when he says he is available

**R1** *(in-game, ~5 min — the highest-value item on the whole list)* · **R4** · **V1** · **V2** ·
**V4** · **V5** · **V6** · **V7** · **V8**

## Critical path

```
R1 ──► detectors viable? ──┐
V2 ──► A3 ──► I2 ──────────┼──► I3 ──► I4 ──► I8 ──► I9-I15
I1 kill hook ──────────────┘
A1 ──► I26
I23 ──► I22 ──► everything server-side
```

**Single best next step: R1.** Built, loaded, takes minutes, and I3 cannot be designed
until the answer is known.

**Best next step needing nobody: I1** — already required for 1.0 independently of Tokens.
**Best server-side start: I23** — the schema blocks all ten other server items.

---

## Done

Moved here with the date and the answer — never deleted.

| Item | Outcome | Date |
|---|---|---|
| **I54** ✅ CHECKPOINT — the D3D11 city works | **Confirmed live, dev-only, 0.84.54.14.** Real per-pixel occlusion, characters included — an occluder the raycast route could never see, since characters are not in the collision mesh. The third and final blocker was found by the one-frame trace: `Render.Camera` holds TWO projections (`ProjectionMatrix2` +0x50, `ProjectionMatrix` +0x1A0) and **both have an all-zero third column**, forcing `clip.z == 0` for every vertex in the world — under reverse-Z the far plane, which fails both our own `Greater` test against a buffer cleared to 0 and the scene comparison. Nearly invisible because everything else was provably right: the trace projected the player to `px(1280.0, 706.3)` and Dalamud's `WorldToScreen` gave `px(1280.0, 706.3)`, pixel-exact, so X/Y/W were perfect and the city could never *look* wrong, only be absent. `Control.ViewProjectionMatrix` (+0x76B0) is the fix, confirmed by Sansflaire as the option that worked. **The reason it works is the keeper: FFXIV's projection is INFINITE-FAR REVERSE-Z**, so the working Z column is *also* three zeros with `M43 = 0.1` — `clip.z` is a constant near plane and all depth rides in `clip.w`, giving `depth = 0.1 / distance`. `M43` is load-bearing, and the intuitive "check M13/M23/M33" test would reject the only matrix that works; `HasDepth` sums all four. This also independently confirms the reverse-Z convention from the matrix rather than from FFXIV-TV's docs. Next: bias tuning (**I56**), alpha (**I55**), fold the diagnostics down (**I57**). | 2026-09-12 |
| **I53** The D3D11 city never drew a pixel — two silent blockers | **Done, dev-only, 0.84.54.11.** I51 shipped and rendered **nothing**, while the panel reported a green `ACTIVE — real geometry, depth-tested per pixel`. Two **independent** faults, either alone sufficient, which is why fixing one and seeing no change looks exactly like fixing nothing. **(1) Colour writes were off.** `new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false }` never assigns `RenderTarget[0]`, and Vortice's struct has **no parameterless constructor**, so the initializer zeroed it — `RenderTargetWriteMask = ColorWriteEnable.None`. A legal, silent value: `CreateBlendState` succeeds and every draw succeeds. *A zero-initialized graphics descriptor is not a defaults struct.* **(2) The matrix was transposed.** The cbuffer declared `float4x4 ViewProj` unqualified; HLSL packs column-major, .NET `Matrix4x4` is row-major, so the shader computed `transpose(M)·v` — which also made `clip.w` garbage and took the depth comparison with it. Both promoted from plausible to certain in minutes by **running the real initializer against the installed Vortice 3.8.2 and disassembling the real shader through the real compiler** (unqualified → four `dp4`s against consecutive registers; `row_major` → the mul/mad chain), which retired an entire planned probe ladder — magenta clear, clip-space triangle, transpose A/B, staging readback — unbuilt. Multiplication order was **not** the bug and was not touched. Third fix, and the one with the longest reach: **a status line may only claim what it measured.** `UsingDepthRenderer` came from `Render()` returning true *on submission*, and the panel turned that into a claim about the screen — §4A question 2 failing in the field. Now `DrawSubmitted`/`DepthSubmitted`, all per-frame counters reset before every early return, and a panel that says `SUBMITTED` and admits nothing reads the finished surface. In-game confirmation is **I54**; the premultiplied-alpha question is **I55**, left unread on purpose. See [BROKEN.md](../BROKEN.md) 017. | 2026-09-12 |
| **I51** Test City: real D3D11 rendering with per-pixel depth occlusion | ⚠️ **Shipped 0.84.54.9 and never drew a single pixel — see I53 for why and the fix.** The architecture below is accurate and survived; two silent implementation faults (a zero colour-write mask and a column-major matrix) meant none of it reached the screen, and the panel's own status line claimed success throughout. **Done, dev-only, 0.84.54.9.** Sansflaire tried the raycast approximation from I50 and it was not good enough, so the city is now genuine geometry. `TestCityD3D` + `CityMeshBuilder`: `CopyResource` the game's depth-stencil into an SRV-bindable copy, draw the city's triangles and gridlines into a plugin-owned colour+depth target at `UiBuilder.Draw` time with a pixel shader that discards anything the scene depth puts behind the world, then blit that surface to the ImGui background list. **Zero render hooks** — ported from FFXIV-TV's `src/CopyBlitRenderer.cs` and XivMediaPlayer's `Compositing/DepthTestedRenderer.cs`, read end-to-end before writing; the hook-based `D3DRenderer` alternative buys only HUD-over-geometry (never needed for an overlay) and costs a `DrawIndexed`/`OMSetRenderTargets` hook its own `Golden-Standard-Rendering.md` says took dozens of versions to stabilise. Every occluder the game drew now counts — characters, foliage, particles — at per-pixel granularity. Load-bearing facts, all from source: **reverse-Z** (Greater, clear to 0, `sceneDepth > myDepth` = hidden), the **game's own ViewMatrix×ProjectionMatrix** off `SceneCamera.RenderCamera` so our clip depth is directly comparable instead of reconstructed, opaque pass with translucency applied once at blit (order-independent, which is what let the painter's sort go), facade detail offset 0.03y off its wall to avoid coplanar z-fighting, and the depth SRV unbound every frame. **The painter's sort and the back-face cull are deleted** — `CullMode.None`, no winding convention left to invert. Raycast and Off remain selectable; any D3D failure falls back to the painted path in the same frame and the panel reports which route actually ran. Vortice is Debug-only, copied into the dev folder (PluginLoadContext resolves only from there), and `Vortice.Direct3D11` is now a release-guard marker. Release artifact unchanged in size; all ten markers absent at both byte parities. | 2026-09-12 |
| **I50** Test City: occlusion, a far larger grid, and a second test on its own tab | **Done, dev-only, 0.84.54.8.** (1) **Occlusion by collision raycast** (`TestCityOcclusion`) — Sansflaire picked it over the depth buffer once all three routes were costed; the two rejected routes are recorded in `CLAUDE.md` §3 so nobody re-costs them. Per drawn face a 3×3 grid of camera rays, cached as packed bits on the building and refreshed 40 faces a frame (nine rays × 240 faces *every* frame would be 2,000+ sustained). Fully hidden faces skip, fully visible ones stay one quad, partial ones draw their visible cells; windows and doors reuse the face's mask for free; edges draw only on a fully visible face, or "occlusion" becomes an X-ray. Limits stated in the panel: collision mesh only (no characters/foliage/particles), blocky edges. (2) **Tiles to 400 and down to 0.05y**, which forced ground measurement to decouple from tile density — its own spacing, capped at 65 samples a side, every height interpolated by `HeightAt`. Tiles therefore stopped being lattice points, so the alignment rule was restated: one shared `TileCorner`/`HeightAt` pair, principle over mechanism. Drawing is bounded independently of content (nearest 64 buildings, fine grid window follows the player) because a cap on content is not a cap on the frame. (3) **Second tab: a circular goal area** (`TestGoalArea`) — wall pulses up from the measured ring and fades, ring measured once with the interior interpolated radially, turns green and fires the objective cue on entry (edge-detected). Occlusion there defaults OFF. Verified absent from the Release DLL on all nine markers at both byte parities. | 2026-09-12 |
| **I49** Test City — a tile grid and coloured buildings drawn into the world | **Done, dev-only, 0.84.54.7.** Sansflaire asked for a Developer → **Test City** panel to try something: a 1:1 tile grid under the character and coloured buildings with windows and a door standing on it. Three gated files — `TestCityService` (placement, one measured lattice), `TestCityRender` (the cuboids), `TestCityWindow` (the panel). The floor goes through `DigVolumeRender.DrawGroundGrid` at stride 1 rather than a second grid drawer, and buildings take their corners **out of that same lattice**, so tile alignment is structural rather than arithmetic that could drift. No depth buffer means visibility and ordering are ours: back-face culling by a 3D normal against the real camera position (`CameraManager.Instance()->Camera->SceneCamera.Position`, field reads only), far-to-near painter's sort over every panel, flat directional shading. The panel counter exists because an inverted cull is indistinguishable from an empty city on screen. Nothing persists — settings are in-memory, deliberately not a `Configuration` shape. Verified absent from the Release DLL at both byte parities. **Not in `HELP.md`:** dev-only, and a section describing something a player cannot reach is worse than none. | 2026-09-12 |
| **I47** Un-gate the Activity tab for public builds | **Done, shipped 0.84.54.0.** It was briefly dev-only and that was a misreading of the request — the tab was asked for with a copy-this-run-for-your-friends button in the same breath, so other people playing it WAS the requirement. The whole dig tree ships; what stayed behind the gate is the LAB (`DigTestsWindow`, `DigMapWindow`) and the `/tchal dig*` commands, the same capability-ships-trigger-does-not split `PropService` uses. vnavmesh became a public requirement and is declared in `README.md` and `docs/HELP.md` and refused at runtime by `ActivityService.Unavailable` — in the pane, not by hiding the tab, because a missing tab teaches the player nothing. Dig artwork now ships and is asserted by `build-public.ps1` (the HUD falls back silently, so a missing PNG had no symptom). Verified against both built DLLs at both byte parities. | 2026-09-11 |
| **I45** Three dig minigame tests + a way to tune their ranges | **Done, all three, dev-only.** **Sense Hunt** (`DigHuntService`): one buried spot, an on-demand 8-point compass Sense whose bearing is a deliberate SNAPSHOT, bands warming RED→YELLOW→GREEN→DIG, timed. **Area Surveillance** (`DigSiteService`): a drawn site with N spaced pieces that assemble into the relic — no warming here, sweeping is the thing being tested. **Clue Trail** (`DigTrailService`): ordered spots, each dig yielding the next clue, with a radar ring that pulses faster approaching and goes SOLID exactly when a dig will land. Ranges live in `DigTuning` (own JSON, live sliders). Everything — rules, commands, buttons, settings — is in `DigTestsWindow` behind `#if DEV_BUILD`; verified absent from the Release DLL. | 2026-09-09 |
| **I46** In-world opaque→transparent gradient walls for box volumes | **Done** (`DigVolumeRender.DrawGradientBox`), used by Surveillance. Banded `AddQuadFilled` — ImGui has no per-vertex gradient for an arbitrary quad, and a projected wall is never screen-axis-aligned so `AddRectFilledMultiColor` does not apply. `PrimReserve`/`PrimWriteVtx`/`PrimWriteIdx` **are** exposed and would give the exact two-triangle version; not taken because `_VtxCurrentIdx` could not be verified (a .NET 10 assembly will not load into PS 5.1 reflection) and a wrong index count corrupts the shared draw list. Upgrade path recorded in `CLAUDE.md` §3. | 2026-09-09 |
| Spawn the Shovel into the player's hands without owning it | **Done, verified unowned 2026-09-09.** `SetupOrnament(57)` attach -> `PlayActionTimeline(13383)` dig -> `SetupOrnament(0)` detach, driven by `/tchallenges shovel` (+ `off`). No ownership at any layer. Three safe routes were eliminated by testing first - Glamourer/weapon override, Brio IPC, Penumbra redirect - and the crashed function came back only under a staged detach-first protocol. See **BROKEN.md 012** and OPEN_QUESTIONS Dead Ends. | 2026-09-09 |
| **R10** Shovel model + animation via the supported route | **Works, and the missing fact is now observed.** Sansflaire owns the Shovel; the game summoned it (`OrnamentId 0 -> 57`) and dismissed it (`57 -> 0`) through its own Fashion Accessory menu while the plugin only watched and played the animation — no memory write at any point. **The game's "no ornament" value is `0`**, which is the number the crashed revision invented as `-1` (see **BROKEN.md 012**). That does not license `SetupOrnament(0,0)` — a field's none-value is not proof of a function's accepted argument — and it is not needed, since the supported route covers every accessory the player owns. | 2026-09-06 |
| **R9** Does the coupled attach→play→auto-detach sequence work? | **Dead — the feature it tested no longer exists.** The ornament attach path was removed one commit after R9 was written, because its teardown (`SetupOrnament(-1)`) hard-crashed the game twice; there is no **Perform** button to run. Nothing is lost: Q17 proved the animation plays with no ornament attached, so the attach was never needed for it. Re-opening this needs a **verified** way to remove an attached model — see **BROKEN.md 012**, and prefer a supported path (Penumbra/Glamourer already attach models every character swap) over poking `OrnamentContainer` directly. | 2026-08-28 |
| **R8** Can an arbitrary `ActionTimeline` row be played on the local player, unowned? | **Yes.** Row 13383 (`ornament_sp/m6017/onm_sp01`) played bare — no ornament attached, nothing owned. `Resident: true` did not require the model. No gate is bypassed: the row is absent from the `Emote` sheet, so `ExecuteEmote`/`IsEmoteUnlocked` never apply. Also corrected the accessory identity — it is the **Shovel** (Ornament row 57), not Fallen Angel Wings; the inferred `6000 + row id` mapping was wrong. See **Q17**. | 2026-08-28 |
| Are mob drop tables in the client sheets? | **No.** Loot is server-side; no `DropList`/`LootTable`/`BNpcDrop`/`MonsterDrop` exists. Hunt routes are kill-count only and cannot chain into Craft. | 2026-08-26 |
| Is `MonsterNote` available for Hunt routes? | **Yes** — `MonsterNote` + `MonsterNoteTarget` both bind normally. | 2026-08-26 |
| Are `IGameInventory` / `ICondition` available? | **Yes, already injected**, and `InventoryWatcher` consumes all six events (but discards the args, so it cannot answer R1). | 2026-08-26 |
| Identity model — name + world + IP? | **IP dropped.** Identity is a local 128-bit secret; `name@world` is a label. Removes the rename problem with no popup. | 2026-08-26 |
| Does the GitHub 60/hr API limit trap a sync-spamming player? | **No.** `FetchAsync` already falls through to `raw.githubusercontent`, which is unlimited. Only consequence is ~5 min staleness. | 2026-08-26 |
| Build the live-probe harness | Shipped dev-only (`/tchallenges probe`), verified absent from the Release DLL. | 2026-08-26 |
| **R6** What is Lumina's `ItemDrop`? | **It does not exist.** The binary-grep hit was a substring of `ItemDropRate`, a `Byte` on `LeveDataStruct`/`CompanyLeveStructStruct` — a levequest reward rate, nothing to do with monsters. **Zero** types in the assembly contain "Drop". | 2026-08-26 |
| **R2** MonsterNote schema | **Resolves, and needed no game session.** `MonsterNote.MonsterNoteTarget` + a **parallel** `Count` collection; `MonsterNoteTarget` gives `BNpcName`, `PlaceNameZone`, `PlaceNameLocation`, `Town`. Trap: `Count` is index-parallel to the target list, like the GlamourDresser bit spans. | 2026-08-26 |
| **Q11 re-verified exhaustively** | Scanned **all 1,198 sheet types**, not four guessed names. No `drop`/`loot`/`spoil`/`booty` type exists; every `reward`/`treasure` match is a scripted reward. **Every `BNpc*` sheet has zero item references** — a mob row cannot point at an item. Settled; do not re-investigate. | 2026-08-26 |
| **I39** Sync cooldown + last-synced label | **Done.** 10 s cooldown lives in `ChallengeSyncService` (`CooldownSeconds`, `CooldownRemaining`) so button, chat command and auto-sync all obey **one** rule rather than three copies. Panache menu folds state + last-synced time into the single existing item ("Sync now — last 14:32" / "Synced 14:32 — wait 7s"); Fallback shows "Wait Ns" with the time in a tooltip. New `CompletionStore.FormatTimeOfDay` keeps the UTC→local conversion in one place. Both flavours clean. | 2026-08-26 |
| **I38** Sync jitter | **Done.** Routine auto-sync now waits a random 0–300 s (`Plugin.StartAutoSync`). **The first-ever sync is deliberately NOT delayed** — one client is not a herd, and an empty list for five minutes on install would trade a real cost for an imaginary one. Needed a `CancellationTokenSource` too: the delay was fire-and-forget, so a dev reload left a task sleeping up to 5 min and waking inside a disposed plugin. Both flavours build clean. | 2026-08-26 |
| **A10** Generated-vs-curated split | **Resolved and shipped.** `data/curated/` overlays are an **input to generation**, not a patch applied after, so regeneration is idempotent and can never destroy curated work. A dataset may carry **several** overlays (`duties.json` + `duties.wiki.json`) so two research pipelines re-run independently. Provenance reaches the header (`curatedFields`/`curatedSource`) and the viewer's CURATED banner. | 2026-08-26 |
| **A4** Monster→zone list | **Done via the Final Fantasy Wiki** (`scripts/wiki/`). Zones went **259 → 755**, and 1,628 monsters gained a duty location. The wiki publishes the **`BNpcName` row id**, so the join is exact, not fuzzy — verified: all 5,035 ids exist in our dataset, 98.3% with matching names. | 2026-08-27 |
| **Places of Interest dataset** | **Built, and from GAME data not the wiki.** `MapMarker` via `Map.MapMarkerRange` gives **6,435 named landmarks across 339 zones** with real map coordinates - settlements, gates, guilds, camps, rivers, aetheryte plazas. The wiki only adds prose (239). Includes the requested `location` column (the zone). | 2026-08-27 |
| **Bosses in their own column** | `duties.bosses` split from `duties.monsters` (156 duties), plus `monsters.isBoss` / `bossKind` for 667 monsters. Boss status is in **no game sheet** - it comes from the wiki's 14 boss subcategories. | 2026-08-27 |
| Is `Fate.Location` a `Level` row? | **No.** All 1,697 values sit inside `Level`'s RowId range and match nothing in it - not RowId, not Object, not EventId. It is an **LGB layer-object id**. This is why `fates.json` shipped with `zone=???` on all 1,712 rows, silently. FATE location must come from the wiki. | 2026-08-27 |
| **gear.acquisition** | **Composed by a local join, no fetching.** `duties.itemsFound` + `monsters.drops` + `fates.rewards` matched against gear names, plus the game-derived `craftable` flag: **12,608 of 28,992**. Required splitting `craftable` (game truth, never overlaid) from `acquisition` (overlay-owned) - the script reads the file it feeds, so craftability could not live only as the string "Crafted" or the second pass would lose it. | 2026-08-27 |
| **A13** Duty bosses | **Done, and better than planned.** Rather than 33 Fandom duty-articles, the Console Games Wiki has a page per duty: `bosses` 156 -> **362 of 373**, `unlockQuest` 259 -> **309**, plus objectives, entrance+coords, time limit, roulette, base EXP. Joined on `id-gt == garlandId` - an EXACT id match, 379 by id and 0 by name. A boss is marked by its difficulty ICON in the heading, not by a ==Bosses== section (only 137 of 529 pages have one). | 2026-08-27 |
| **Monster DROPS** | **Solved by an external source.** ffxiv.consolegameswiki.com has one page per enemy with a Loot section: **1,129 monsters with loot, 3,172 explicitly None, 4,185 still ???**. Also raised zones 755 -> 8,503 and level -> 6,384. "No drops" is now recorded as `None`, NOT `???` - most monsters drop nothing and a false unknown would send the generator hunting for data that does not exist. | 2026-08-27 |
| FATE chain ordering | **Not irreducible after all.** `FATEChain` groups a chain but never sequences it; the Console Games Wiki's `prev-fate`/`next-fate` is the sequence. 233 FATEs are actually chained. | 2026-08-27 |
| **A6** Fill `data/monsters.json` | **Largely done.** 3,504 of 14,560 entries curated: level (3,401), hp, hitbox, abilities, family, creatureClass, zones, duties, fates, quests, dungeonEnemy/Boss. `drops` stays ??? forever — server-side, settled at Q11. Garland was correctly ruled out; the wiki was the right source. | 2026-08-27 |
| Where do instance monsters come from? | **`Final Fantasy XIV enemies/<class>` subpages**, not the ~9,000 individual enemy pages — those are redirects. 19 subpages, **23 requests**, 5,294 rows. Four parsing traps documented in `scripts/wiki/README.md`; every one produced plausible-but-wrong data rather than an error. | 2026-08-27 |
