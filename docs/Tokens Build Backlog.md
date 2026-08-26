# Challenge Tokens — Build Backlog (everything that does not exist yet)

Created 2026-08-26 at Trist's request: *"Can you create a list of everything that we DON'T have
that we discussed wanting to get created."*

**Scope:** every component discussed for Challenge Tokens + randomized quests that has **no code
today**. Verified by grepping the source tree, not from memory — there is currently **zero** Token,
account, or quest-generation code in the plugin.

Design rationale for any line here: [`Challenge Tokens and Quests.md`](Challenge%20Tokens%20and%20Quests.md).
Things needing a *decision* or an *in-game test* rather than construction:
[`Pending Verification.md`](Pending%20Verification.md).

**Status:** ⬜ nothing exists · 🟨 something exists to build on · ⚠️ blocked

---

## SECTION INDEX

| # | Section | Anchor |
|---|---------|--------|
| 0 | Critical path | `path` |
| 1 | Data & content pipeline | `data` |
| 2 | Detection | `detect` |
| 3 | Client — accounts | `accounts` |
| 4 | Client — quests & tokens | `client` |
| 5 | Server — Cloudflare | `server` |
| 6 | Web surfaces | `web` |
| 7 | Rewards | `rewards` |
| 8 | Not blocking, easy wins | `easy` |

---

<!-- SECTION:path -->
## 0. Critical path

Order matters — several items are worthless until the one before them lands.

```
V1 probe run ──► detectors viable? ──┐
                                     ├──► quest generator ──► server ──► client UI
curated materials list ──────────────┤
kill hook (Hunt routes) ─────────────┘
```

1. **V1 probe run** (`/tchallenges probe`) — decides whether Gather/Craft are detectable at all.
   Everything downstream assumes yes. **Tool built, never run.**
2. **Curated raw-materials list** — the generator's input. Trist owns the contents.
3. **Kill hook** — Hunt routes cannot exist without it, and it was already required for 1.0.
4. **Generator** — needs 2 and 3.
5. **Server** — can be built in parallel with 4.
6. **Client UI** — needs 4 and 5.

---

<!-- SECTION:data -->
## 1. Data & content pipeline

| # | Item | Status | Notes |
|---|---|---|---|
| D1 | **Curated raw-materials list** | ⬜ | **Trist's plan, 2026-08-26:** we own and maintain our own list of raw materials and where each comes from, and work off it permanently — extended when we add to it, never regenerated. Trist will go over the contents and schema later. This replaces any notion of live third-party lookup. |
| D2 | Extraction workflow for D1 | ⬜ | One-time dev-side pass over community DBs (Garland Tools et al.) to seed the list. Never runs on a player's machine. Check redistribution terms before bulk-copying a community dataset. |
| D3 | Quest generator (backward-chaining) | ⬜ | Capstone → recipe → ingredient sourcing → topological steps. Also derives difficulty, Token value and minimum plausible completion time from the same walk. |
| D4 | Exclusion-rule data | ⬜ | Savage/extreme from current **or previous** expansion, and beast-tribe-currency items. Must apply across the capstone's ENTIRE ingredient tree. The expansion window must be data, not a constant. |
| D5 | Bracket definitions | ⬜ | Level bands per route. Q15, deferred by Trist. |
| D6 | `active.json` + publisher | ⬜ | Names the live Hourly/Daily/Weekly quest ids. Publisher extends the existing Creator→Publish path. |
| D7 | Quest archive | ⬜ | Every quest ever issued, retained. Small. |

---

<!-- SECTION:detect -->
## 2. Detection

| # | Item | Status | Notes |
|---|---|---|---|
| T1 | **Kill tracking hook** | ⬜ ⚠️ | `ActionEffectHandler.Receive` + `HookFromAddress`, DamageMeter's pattern. **Verified absent from the source tree 2026-08-26** — Q7 approved this on 2026-08-22 and called it *required for 1.0*, but it was never built. Blocks every Hunt route AND the existing enemy-challenge milestone. |
| T2 | Enemy/kill condition type | ⬜ | No `Enemy`/`Defeat`/`Kill` member exists in `ConditionType`. Needs the taxonomy from Q7 (`ModelChara.Type`, `BNpcName`, name matching — no plant/beast family exists). |
| T3 | Provenance capture (gather vs craft vs buy) | 🟨 | `LiveProbe` captures it for investigation; the **production** path does not exist. `InventoryWatcher` deliberately discards event args. Depends on V1's result. |
| T4 | Turn-in / spend detection | ⬜ | `ItemRemoved` + vendor addon. Unverified. |
| T5 | Minimum-plausible-completion-time model | ⬜ | Falls out of D3's graph walk; nothing computes it. |

---

<!-- SECTION:accounts -->
## 3. Client — accounts

| # | Item | Status | Notes |
|---|---|---|---|
| A1 | Local account secret (128-bit) | ⬜ | Generated silently on first run. **This is the identity.** |
| A2 | Account tier setting | ⬜ | Local-only / Anonymous (default) / Lodestone-linked. Tier 0 must make **zero** requests. |
| A3 | Local Token cache | ⬜ | Obfuscated. Friction, not security — a tripped tamper check logs, never punishes. |
| A4 | Recovery popup | ⬜ | Fires **only** when the local secret is missing AND the server has a Tokened account matching name+world. Not on rename — rename needs no popup at all. |
| A5 | Lodestone link flow (client half) | ⬜ | Request token → display it → "I've added it" → poll for verification. Entirely optional, never nagged. |
| A6 | Offline queue + retry | ⬜ | Buffer completions through a network blip. Low priority — an online-only MMO makes true offline play rare. |

---

<!-- SECTION:client -->
## 4. Client — quests & tokens

| # | Item | Status | Notes |
|---|---|---|---|
| C1 | Quest definition sync | 🟨 | Extend `ChallengeSyncService` to fetch `active.json` + quest bodies. The fetch/verify/cache machinery already exists and already falls back to raw on API rate-limit. |
| C2 | Sync jitter | ⬜ | Random 0–300 s offset so an hourly rotation does not stampede. Trist notes the scenario is unlikely; it is ~3 lines. |
| C3 | Manual "check for current quest" button | ⬜ | Plus a short cooldown and a "Last synced HH:MM" label — the right fix for click-mashing, since the rate limit is already handled by fallback. |
| C4 | Quest display UI | ⬜ | Tier × Route × Bracket, showing only the hardest bracket the player qualifies for. |
| C5 | Bracket eligibility evaluation | ⬜ | From the player's own job levels, client-side. |
| C6 | Quest instance state machine | ⬜ | accept → steps → complete, with local persistence. |
| C7 | **Breadcrumb recorder** | ⬜ | **Confirmed IN by Trist 2026-08-26.** Record each step locally with a timestamp; send the whole trail **attached to the completion**, never per-step. One request per quest instead of ~7 — the difference between 15k and 105k requests/day at 5k actives. |
| C8 | Completion submission | ⬜ | Posts instance id + breadcrumb trail. **Never posts a Token value.** |
| C9 | Token balance display | ⬜ | Lifetime (score, never falls) shown separately from Balance (spendable). |
| C10 | Token spending UI | ⬜ | Needs §7. |

---

<!-- SECTION:server -->
## 5. Server — Cloudflare

A `worker/` directory already exists (`suggestions-worker.js`, `wrangler.toml`) for the Discord
relay, so the account, CLI and deploy path are proven. **None of the Token server exists.**

| # | Item | Status | Notes |
|---|---|---|---|
| S1 | Token API Worker | ⬜ | `accept`, `complete`, `balance`, `resync`. Separate from the suggestions worker. |
| S2 | D1 schema | ⬜ | accounts · **append-only ledger** · quest instances · label history · review queue. Lifetime/Balance are views over the ledger, never stored totals. |
| S3 | Idempotency | ⬜ | One instance awards Tokens at most once, ever. |
| S4 | Server-side Token value lookup | ⬜ | The structural anti-cheat: the client says *what* completed, the server decides *what it is worth*. |
| S5 | **`TOKEN_PEPPER`** | ⬜ | Verified absent. Back up to (1) private vault repo, (2) local PC beside `SansflaireCertificate\`, (3) `SECRETS.local.md`, then `wrangler secret put`. **Losing it makes every stored `identityHash` unlookupable forever.** |
| S6 | Per-row salt | ⬜ | **Trist 2026-08-26: do it anyway** — "it's just a little extra." Stored per account row alongside the peppered HMAC. |
| S7 | `ADMIN_KEY`, `LODESTONE_UA` | ⬜ | See `SECURITY.local.md` §6. |
| S8 | Lodestone verification scraper | ⬜ | Queued, rate-limited ~1/sec, **one-shot only** — at link time and on manual re-link. Never scheduled. Target the numeric character ID (survives renames and world transfers). |
| S9 | Materialized leaderboard | ⬜ | Computed on a cron, served cached. Never per-request. |
| S10 | Review queue table + admin endpoints | ⬜ | Includes `pluginVersion` — the column that turns a report into a bug fix. |
| S11 | Anomaly detection batch | ⬜ | Cron, not inline. Thresholds live in `SECURITY.local.md`. |
| S12 | Ban integration | ⬜ | Suspends accrual; never deletes the ledger. Unban is a flag flip. |

---

<!-- SECTION:web -->
## 6. Web surfaces

| # | Item | Status | Notes |
|---|---|---|---|
| W1 | Public profile / leaderboard | ⬜ | Trist: no public display of who uses the plugin. Hashed identity means opt-in display names if ever wanted. |
| W2 | Admin review view | ⬜ | Trist is the only admin for now. |
| W3 | Resync endpoint for a wiped install | ⬜ | Nearly free once S2 exists — the local file is only a cache. |

---

<!-- SECTION:rewards -->
## 7. Rewards to spend Tokens on

None exist as *purchasable*. The good news is most reuse a working system, so they are cheap.

| # | Item | Status | Reuses |
|---|---|---|---|
| R1 | Purchasable backgrounds | 🟨 | `BackgroundLibrary` |
| R2 | Alternate completion fanfares | 🟨 | `SoundService`, `GameSound` |
| R3 | Fly-text styles | 🟨 | `FlyTextService` |
| R4 | Progress-toast styles | 🟨 | `ProgressToast` |
| R5 | Accent colours / themes | 🟨 | `ChallengeThemes`, `Palette` |
| R6 | Titles / flair | ⬜ | New, small |
| R7 | Prestige quest unlocks | ⬜ | New |
| R8 | Ownership/purchase model | ⬜ | The actual gap: something must record what was bought and gate it. |

---

<!-- SECTION:easy -->
## 8. Not blocking — cheap wins available now

- **C2 sync jitter** — a few lines, prevents a whole class of scaling problem.
- **C3 cooldown + "last synced" label** — pure UX, no dependencies.
- **S5 pepper generation + backup** — do it before anything needs it, not during.
- **T1 kill hook** — already required for 1.0 independently of Tokens, and unblocks Hunt routes.

---

## Decisions recorded here

| Date | Decision |
|---|---|
| 2026-08-26 | **Breadcrumbs are batched**, attached to the completion. Confirmed by Trist. |
| 2026-08-26 | **Per-row salt in addition to the server-side pepper.** "It's just a little extra." |
| 2026-08-26 | **We own a curated raw-materials list** and work off it permanently. Contents/schema to be defined with Trist later. |
