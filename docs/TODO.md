# TODO — Challenge Tokens & Randomized Quests

**This is the checklist and the single source of STATUS.** Tick items here. The companion docs
hold the reasoning and must not track status themselves, or the three will drift:

- [`Tokens Build Backlog.md`](Tokens%20Build%20Backlog.md) — why/how detail for every build item
- [`Pending Verification.md`](Pending%20Verification.md) — detail + the Completed answer log
- [`Challenge Tokens and Quests.md`](Challenge%20Tokens%20and%20Quests.md) — the design record

Created 2026-08-26. Nothing on this list is built yet.

## The rules (Trist, 2026-08-26 — see `CLAUDE.md` §6A)

1. **This is THE official list.** We work off it. Trist requests something → it goes on with a
   permanent ID. We change something in the project → **its item comes off, in the same commit as
   the change.** Not later, not in a cleanup pass. *(Implemented as "move to Done with the date",
   matching the `OPEN_QUESTIONS.md` convention — off the active list either way.)*
2. **Assume Trist cannot test.** If he asks for the next thing to work on and has not said he is
   at home, offer only a **🤖** item — completable with nobody but Claude. Never propose an
   in-game action, a decision, or visual sign-off unless he says he is available.
3. **Priority order**, applied as a tiebreak chain top-down:
   **(a)** quick/easy/simple → **(b)** unblockers, easiest first → **(c)** non-client/host/web
   (core, data, detection before UI, server, web) → **(d)** everything else.

**IDs are permanent.** Never renumber — other docs and commit messages reference them. Add new
items with the next free number in their block; leave gaps where things are dropped.

**Tags:** 🤖 Claude can do this alone · ⚡ cheap · 🔴 blocks other work · 🔒 needs a secret ·
🙋 **needs Trist** (in-game action, decision, or sign-off)

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

## 📋 Review & decide — needs Trist

- [ ] **V1** 🙋 Public repo history — leave the already-pushed anti-cheat §13, or rewrite? *(rec: leave)*
- [ ] **V2** 🙋 🔴 Curated raw-materials list — contents + schema *(Trist owns this)*
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
- [ ] **A4** 🙋 **Compile our own monster→zone list.** The game gives zones for only **259
      monsters (1.8% of 14,560 named)** — the Hunting Log, low-level only. Needs online data;
      Trist will help. Without it, Hunt routes are capped at that small low-level pool and
      high-level Hunt routes have no zone source at all.
  - ⛔ Blocked by: **R5** *(same redistribution question as the materials list)*

## 🔨 Implement

### Blocking
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

### Cheap wins — no dependencies, any time
*(all done — add new ones here)*

---

## Startable right now — nothing blocks these

### 🤖 Claude can do these alone — the answer to "what's next?" when Trist is away

Listed in **rule 3 priority order**, so the top item is the default recommendation:

| # | Item | Why it ranks here |
|---|---|---|
| 1 | **I7** Exclusion data as data, not a constant | (a) small, (c) core/data |
| 2 | **I16** Local account secret | (a) small, (c) core |
| 3 | **I17** Account tier setting | (a) small, (c) core |
| 4 | **I18** Obfuscated local Token cache | (a) small, (c) core |
| 5 | **I1** Kill hook + `Enemy` condition | (b) **biggest unblocker** — frees R3, I3, and the 1.0 enemy milestone. Harder than the above, so it ranks below them |
| 6 | **I23** D1 schema | (b) unblocks ten server items, but (c) demotes it — it is host work |
| 7 | **A1** Generate + back up `TOKEN_PEPPER` | 🔒 unblocks I26; do before anything needs it |
| 8 | **A2** `ADMIN_KEY`, `LODESTONE_UA` | 🔒 unblocks I27 |
| 9 | **R5** Garland Tools terms | Research; unblocks A3, but the answer may be "ask a human" |

### 🙋 Needs Trist — only offer these when he says he is available

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
