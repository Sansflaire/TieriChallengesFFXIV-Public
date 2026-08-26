# Challenge Tokens & Randomized Quests — Design Record

**Status: BRAINSTORM / DESIGN. Nothing here is built.** No code exists for any of it. This
document records the design conversation of 2026-08-25/26 so the decisions survive the session.

It supersedes **Q10** in [`../research/OPEN_QUESTIONS.md`](../research/OPEN_QUESTIONS.md)
("Any reward beyond a checkmark? — No, completion plus its date is the whole reward"). That
answer stood from 2026-08-22 until this conversation reversed it.

---

## SECTION INDEX

| # | Section | Anchor |
|---|---------|--------|
| 1 | Terminology | `terminology` |
| 2 | The three account tiers | `accounts` |
| 3 | Identity: why the local secret replaces IP | `identity` |
| 4 | The rename problem, solved | `rename` |
| 5 | Lodestone linking | `lodestone` |
| 6 | Quest distribution via the public repo | `distribution` |
| 7 | Sync cost — the real numbers | `synccost` |
| 8 | Quest structure: tier × route × bracket | `structure` |
| 9 | Generation: backward-chaining | `generation` |
| 10 | Detection — what is confirmed | `detection` |
| 11 | Token economy and spending | `economy` |
| 12 | Server architecture | `server` |
| 13 | Anti-cheat posture | `anticheat` |
| 14 | The review queue | `review` |
| 15 | Decision log | `decisions` |
| 16 | Open items | `open` |

---

<!-- SECTION:terminology -->
## 1. Terminology

| Term | Meaning |
|---|---|
| **Challenge Tokens** / **Tokens** | The point currency. Decided 2026-08-26. |
| **Lifetime Tokens** | Total ever earned. **Never decreases.** This is the player's score. |
| **Balance** | Spendable Tokens. Decreases on purchase. |
| **Quest definition** | The shared, public description of a quest. Same for every player. |
| **Quest instance** | One character's attempt at a definition. Server-side row. |
| **Tier** | Hourly / Daily / Weekly. |
| **Route** | Hunt / Craft / Gather. Three ways to close one tier. |
| **Bracket** | Level band a route is authored for (see §8). |

Tracking Lifetime and Balance separately is a hard requirement — **a player's score must never
go down because they spent Tokens.**

---

<!-- SECTION:accounts -->
## 2. The three account tiers

Driving constraint, in Trist's words: *"The goal is to never burden the user with forcing them
to enter data or do things that are not plain ol' 'play the game'. I hate pop-ups,
advertisements, account bullshit, etc myself."*

**The plugin must be fully functional with only a download.** Everything below is opt-in or
invisible.

| Tier | What exists | Recovery | Visible to player? |
|---|---|---|---|
| **0 — Local only** | Tokens in the local config. No network writes, ever. | None | Only if they go looking for the setting |
| **1 — Anonymous (default)** | Random 128-bit secret + server row. No PII. | While the local file survives | **Nothing.** No prompt, no signup |
| **2 — Lodestone-linked** | Tier 1 + verified Lodestone character ID | Permanent, provable | One voluntary action |

Tier 1 is created **silently on first run** — no prompt, no dialog, no "create an account?"
The player just plays and Tokens accrue. That satisfies both "auto-created for them" and
"never burden the user," which a signup flow could not.

Tier 0 must be reachable from settings and must be honoured absolutely: no requests at all,
not even anonymous ones.

---

<!-- SECTION:identity -->
## 3. Identity: why the local secret replaces IP

Trist's proposal was to key accounts on **player name + server + IP at time of creation**.

**Recommendation: drop IP entirely.** It costs real risk and buys close to nothing:

- **Residential IPs are dynamic.** Router reboot, ISP lease renewal, or a phone hotspot all
  change it. The "something changed, are you still you?" popup would fire constantly for
  ordinary players who changed nothing — the exact pop-up spam the design is trying to avoid.
- **CGNAT makes IPs shared.** Many players legitimately present the same IP. It is not unique.
- **VPN users churn it constantly.**
- **It is personal data under GDPR**, which raises the compliance stakes for a hobby plugin
  storing data on EU players.
- **It authenticates nothing.** Anyone who knows a name+world can claim it regardless.

**What to use instead — a locally-generated account secret.** On first run the plugin generates
128 random bits, stores them locally, and sends them with every request. *That* is the identity.

```
accountSecret  = 128 random bits, generated locally, never derived from anything
identityHash   = HMAC(serverPepper, "name@world")   ← computed server-side only
displayLabel   = name@world                          ← a label, not an identity
```

Consequences, all good:

- **Name+world becomes a label, not a key.** A rename cannot orphan anything (§4).
- **No PII is required** for a functioning account.
- The secret is a bearer token — whoever holds it owns the account. That is fine at this
  stake level, and Lodestone linking (§5) is the upgrade for anyone who wants real ownership.

### Hashing name+world

Trist: *"If we can change to hashing player name + server I'd want to change to do that fully."*

Do it, with one correction. FFXIV `name@world` is **low-entropy** — the world list is public
and names are dictionary-ish, so a plain salted hash in a leaked database is enumerable by
brute force. Use an **HMAC with a server-side pepper** that never ships to clients:

- Client sends `name@world` in plaintext **over TLS**, transiently, in the request body.
- Server HMACs it with the pepper and stores **only the hash**. Plaintext never hits disk.
- Admin lookup still works: a player writes in claiming an old name, you HMAC the claim and
  search for it. **The support workflow Trist wants is fully preserved.**

This mirrors the reasoning already written into [`BanService`](../src/BanService.cs) — hashes
so the stored file "can only ever answer 'is THIS identity banned?', one guess at a time."
Same principle, one level stronger because the pepper is not in the shipped DLL.

---

<!-- SECTION:rename -->
## 4. The rename problem, solved

The original concern: a player pays for a name change, and their score is wiped.

**With a local secret, this problem disappears entirely and needs no popup.** The server keys
on the secret; the name is just a label. On rename, the plugin notices the label changed and
silently sends the new one. The player sees nothing and loses nothing.

Keep an **append-only label history** on the account row — every `(identityHash, firstSeen,
lastSeen)` the account has ever presented. That gives exactly the backwards-compatible admin
trail Trist asked for: a player writes in saying "I used to be Foo@Balmung," you HMAC it,
find the row, restore.

### When a popup IS needed

Only in the **recovery** case: the local secret is missing (fresh install, new PC, wiped
config) *and* the server has a Tokened account matching this character's name+world.

Then, once, on first window open:

> We found **1,240 Challenge Tokens** for a character with this name.
> Is this you?  **[Yes, restore]  [No, start fresh]  [Don't ask again]**

That is Trist's proposed flow, preserved — but fired in the rare case it is genuinely useful
rather than on every dynamic-IP change.

**Note the claim-transfer hole:** if someone renames *into* a name a scoring player used to
hold, they would be offered that account. Stakes are low and Trist has said cheating is not a
serious concern, so the proposed handling is: auto-merge silently below a Token threshold,
require Lodestone verification above it.

---

<!-- SECTION:lodestone -->
## 5. Lodestone linking

The flow Trist described is exactly right and is what Discord FC bots already use:

1. Server issues a short random token.
2. Player pastes it into their Lodestone character profile comment.
3. Server fetches the public profile, confirms the string, marks the account verified.
4. Player deletes the comment.

**One important correction: link to the numeric Lodestone character ID, not the name.** The ID
is stable across both **renames and world transfers**, which makes it the single most durable
identity available anywhere in this system.

Three things linking buys:

- **Permanent recovery.** Lost config, new PC, anything — provable re-claim.
- **Bracket verification (§8).** Lodestone publishes class/job levels, so for linked accounts
  the server can independently confirm a player was eligible for the bracket they claimed.
  This is the only server-side verification available that does not trust the client at all.
- **A trust anchor** for the review queue.

Scraping is done **by the server, not the game client**, so it adds no Square Enix risk to the
player's account.

Linking must stay **entirely optional** and unprompted. Surface it in settings and mention it
once in the help panel; never nag.

---

<!-- SECTION:distribution -->
## 6. Quest distribution via the public repo

Decided: reuse the existing sync path rather than invent one.

- Quest definitions are published to the **public Sync repo**, exactly as challenges already are
  via [`ChallengeSyncService`](../src/ChallengeSyncService.cs) and `OfficialCatalog`.
- A small pointer document (`active.json`) names the currently-live Hourly / Daily / Weekly
  quest IDs.
- Every quest that has ever existed stays in the repo as a growing archive. Only the newest is
  active. The data volume is trivial.
- The client syncs on plugin load and caches.

**Past-due completions are accepted.** Trist: *"If a player completes a challenge that is
past-due but they never synced so it still is active for them, that received completion + score
awarded should be allowed to still go through."* The server does **not** reject on expiry.

This means expiry cannot be used as a hard anti-cheat invariant — it becomes a soft signal for
the review queue instead (§13).

The GitHub repo handles **read-only broadcast**. It cannot handle Token writes; that is what
the server in §12 is for. Two different jobs, two different tools.

---

<!-- SECTION:synccost -->
## 7. Sync cost — the real numbers

Trist asked how much strain frequent syncing would realistically put on the network/game.

**Answer: effectively zero, and the existing implementation is already the safe shape.**
Verified in [`ChallengeSyncService.cs`](../src/ChallengeSyncService.cs): `SyncAsync` is fully
async with `ConfigureAwait(false)` throughout, so it never blocks the framework thread. Nothing
about a fetch can stutter the game.

| Quantity | Value |
|---|---|
| Payload (`active.json` + a quest body) | ~1–10 KB |
| Requests per sync | 1–2 HTTPS GETs |
| Latency | ~50–300 ms, off-thread |
| FFXIV's own sustained traffic | roughly 10–40 KB/s in a populated zone |

A sync costs **less than one second of the game's ordinary network use.** Bandwidth is a
non-issue and always will be.

**The only real constraint is GitHub's unauthenticated API rate limit: 60 requests/hour/IP**,
already documented in `BanService`. That is a budget to respect, not a bandwidth problem.

### Recommended policy

- **Sync on plugin load** — matches existing behaviour.
- **Lazy re-sync**: when the main window opens, if the active-quest pointer is older than the
  current hour boundary, fetch. Otherwise use cache.
- **Manual "Check for current quest" button**, always available. (Trist asked for this
  regardless — agreed, and it doubles as the fix for any staleness complaint.)
- **No background timer.** Nothing fetches while the player is just playing.

Typical session: **1–3 fetches total.** Worst realistic case — a player opening the window every
hour — is 24/day against a 60/hour ceiling.

Use the **raw.githubusercontent URL for routine polling** (no rate limit, ~5 min stale, which is
irrelevant for an hourly rotation) and reserve the **API URL for post-publish freshness**, which
is exactly the split `BanService` already documents and measured.

---

<!-- SECTION:structure -->
## 8. Quest structure: tier × route × bracket

```
Tier (Hourly | Daily | Weekly)
 └── Route (HUNT | CRAFT | GATHER)     ← player picks one
      └── Bracket (level band)          ← client offers the hardest they qualify for
```

**Completing any one route closes the entire tier for that window.** Doing the Weekly HUNT
denies the Weekly CRAFT and GATHER and awards the Weekly Tokens once. Confirmed by Trist.

### Brackets and the anti-cheese rule

Every tier publishes routes for **all level brackets** so that every player, at any level, has
something possible. But the client offers only the **hardest bracket the player qualifies
for** — no crafting a level 10 weapon at level 100 crafting to cheese the same Tokens.

Since quests are global and shared, eligibility is necessarily evaluated **client-side** from
the player's own job levels, which are trivially readable and certain. The server records the
claimed bracket. For **Lodestone-linked accounts the server can verify it independently** (§5);
for anonymous accounts a mismatch pattern goes to the review queue, not to a ban.

Optional flavour toggles — visit a zone, equip specific gear, emote, mount — are **guidance
only, never real objectives.** Confirmed by Trist. They must never gate completion.

**Never require "reach level N."** Explicitly ruled out.

---

<!-- SECTION:generation -->
## 9. Generation: backward-chaining from the capstone

Generate multi-part quests by **backward-chaining from the reward**, never by forward-assembling
random steps. Forward assembly produces incoherent chores ("kill 5 rats, craft a bronze sword,
turn in a carrot"). Backward chaining produces the coherent hunt→gather→craft→turn-in chain
Trist described, for free, out of the recipe tree.

1. Seed picks a **capstone item** from `Recipe` at the target bracket.
2. Expand its recipe one level → ingredients.
3. For each ingredient, resolve a **sourcing verb** from game data:
   - gatherable → **Gather** step
   - craftable → **recurse**, depth-capped at 2–3
   - vendor-buyable → **Purchase** step, or assume owned
   - mob drop → **Hunt** step ⚠️ see the blocker below
4. Emit steps in topological order. That is the quest.
5. **Difficulty, Token value, and minimum plausible completion time all fall out of the same
   graph walk** — depth × breadth × bracket — instead of being hand-tuned per quest.

### ✅ The blocker — RESOLVED 2026-08-26

**Mob drop tables are NOT in the client sheets.** Verified by probing `Lumina.Excel.dll`'s
metadata in the installed API 15: no `DropList`, `LootTable`, `BNpcDrop`, or `MonsterDrop` type
exists. Loot is server-side, as suspected. (`InstanceContentReward` and `ContentsNote` exist but
are duty rewards, not mob loot.)

**Consequence — this shapes the generator:**

- **Hunt routes are kill-count only.** "Defeat 12 Ixali" — never "hunt X to obtain material Y."
- **Hunt cannot chain into Craft.** A multi-part quest's material steps must come from
  **Gather**, from a **vendor**, or from an intermediate **craft**. This is a real narrowing of
  the multi-part design and needs to be accounted for when the step structure is designed (Q14).
- **`MonsterNote` and `MonsterNoteTarget` both exist** and are the source for Hunt routes.

Every other generator sheet is present and binds normally: `Recipe`, `RecipeLookup`,
`GatheringItem`, `GatheringPointBase`.

**`GilShopItem` is a subrow sheet** (`IExcelSubrow<T>`, not `IExcelRow<T>`) — discovered at
compile time. Vendor sourcing needs `GetSubrowExcelSheet<T>()`, not the ordinary accessor.

### 9a. Drop data comes from a curated offline dataset

Decided by Trist 2026-08-26: since the game ships no drop data, **community databases are the only
source**, e.g. Garland Tools (`garlandtools.org/db/#item/5554` → Morbol Vine → Drop → mob list →
mob pages give locations). The same applies to NPC shop inventories.

**The plugin must never scrape at runtime.** The pipeline is:

```
Garland Tools / community DB  →  dev-time extraction  →  curated drops.json
        →  published to the Sync repo  →  client reads it like any other quest data
```

Three reasons this is not negotiable: 1,000 clients hitting a community site is abuse of a free
service; a third-party outage would break quest generation for everyone; and it puts an
uncontrolled network dependency inside the game client. The dataset is small, changes only on
patch days, and belongs beside the quest definitions.

**Kill-detection is unaffected.** The `ActionEffectHandler` hook already identifies mobs by
`BNpcName`; the external dataset only supplies the item→mob→zone *mapping* used to author a quest,
never the runtime detection.

### 9b. Content exclusion rules — what the generator must refuse

Trist's constraints, 2026-08-26. A generated quest must **never** require:

| Excluded | Why |
|---|---|
| **Savage raid drops** from the current or previous expansion | Gates a daily behind endgame raiding |
| **Extreme trial drops** from the current or previous expansion | Same |
| **Beast tribe currency** items | Makes beast tribe grinding a prerequisite |

Explicitly **fair game**: normal dungeon drops, anything gatherable, gil-vendor items, and
**completing a beast tribe quest itself** as a daily/weekly objective (the *quest* is fine; the
*currency* is not).

Implementation note: exclusion is a property of the **capstone's whole ingredient tree**, not just
its top-level recipe. Backward-chaining must reject a capstone the moment ANY node in its expanded
tree resolves to an excluded source — otherwise a level-3 ingredient quietly reintroduces the
savage drop the top-level check passed.

⚠️ "Current or previous expansion" is a **moving window** — it must be data, not a hardcoded
constant, or every generated quest silently becomes non-compliant at the next expansion launch.

---

<!-- SECTION:detection -->
## 10. Detection — what is confirmed

Trist's absolute rule: *"We'll only ever work with data the game/plugin/dalamud/Claude can
detect with certainty."* Every verb needs a proven detector before it enters the pool.

### ✅ Kill tracking — already answered, do not re-derive

This was flagged "remind me to confirm later," but the project already settled it on
2026-08-22. From [`OPEN_QUESTIONS.md`](../research/OPEN_QUESTIONS.md):

- **Q7 (answered):** enemy challenges use an **`ActionEffectHandler.Receive` hook** following
  DamageMeter's pattern. Filtering by `ModelChara.Type`, specific `BNpcName`, and name matching.
- **Dead end (recorded):** watching the object table for an HP-zero transition **cannot
  attribute the kill** — anything dying nearby would count. The hook is mandatory.
- Working reference implementation: `DamageMeter/src/CombatTracker.cs`
  (`ActionEffectHandler.Addresses.Receive.Value` + `HookFromAddress`).
- Also recorded: **no plant/beast taxonomy exists** in game data. `BNpcBase` has no family
  column. Only `ModelChara.Type` (1=human, 2=monster, 3=demihuman), `BNpcName`, and
  name-substring matching resolve.

So: **kill mob X, count N — yes, with the hook. Approved and pattern identified.**

### ✅ Gather / Craft / Obtain — verified this session

Trist flagged this as critical. **Verified against the installed API 15 Dalamud** at
`addon/Hooks/dev/Dalamud.dll`: `IGameInventory` exists, with `InventoryChanged`, `ItemAdded`,
`ItemRemoved`, `ItemChanged`, `ItemMoved`, plus `GameInventoryItem`, `GameInventoryEvent`,
`GameInventoryType` and typed args (`ItemAddedArgs`).

**The provenance problem and its fix.** `ItemAdded` says an item arrived; it does **not** say
*how*. Gathered, crafted, bought, traded, and pulled from a retainer all look identical. That
matters enormously — otherwise "gather 20 copper ore" is satisfied by buying it.

**Fix: sample `ICondition` at the instant of the event.** `Gathering` set → it was gathered.
`Crafting` set → it was crafted. This gives provenance from two confirmed-present APIs with no
hooks and no guessing.

### Verb table

| Verb | Detector | Status |
|---|---|---|
| Gather | `ItemAdded` + `Condition[Gathering]` | ✅ APIs confirmed present |
| Craft | `ItemAdded` + `Condition[Crafting]` | ✅ APIs confirmed present |
| Obtain / Turn in | `ItemAdded` / `ItemRemoved` | ✅ APIs confirmed present |
| Hunt N of mob M | `ActionEffectHandler.Receive` hook | ✅ Approved, pattern known |
| Visit zone / area | Existing area engine | ✅ Already shipping |
| Equip / Emote / Mount | `PlayerStateReader` | ✅ Already shipping |
| Reach level | — | ⛔ **Ruled out by Trist** |

**Correction (2026-08-26):** an earlier revision of this document claimed none of these services
were injected yet. That was wrong — it came from a truncated grep, and absence of output was
read as absence of code. `ICondition`, `IGameInventory` and `ITargetManager` are **already
injected** ([`Plugin.cs`](../src/Plugin.cs)), and [`InventoryWatcher`](../src/InventoryWatcher.cs)
already consumes all six inventory events.

**But `InventoryWatcher` cannot answer Q13.** It deliberately discards the event args and only
sets a dirty flag — a documented design choice, because applying the six event kinds as deltas
is fiddly and a desynchronised map silently breaks challenges. So provenance needs its own
capture path, which is what [`LiveProbe`](../src/LiveProbe.cs) is for.

Also already available and relevant: `GameStateFlag.Gathering` / `.Crafting` already exist in
[`ChallengeConditions`](../src/ChallengeConditions.cs), so the flags the provenance test depends
on are already modelled in this plugin.

---

<!-- SECTION:economy -->
## 11. Token economy and spending

| Tier | Frequency | Suggested value |
|---|---|---|
| Hourly | rotates ×24/day | 10–25 |
| Daily | 1 window | 50–200 |
| Weekly | 1 window | 500–1500 |

**Lifetime Tokens and Balance are tracked separately.** Spending reduces Balance only; the
score never falls.

### What to spend Tokens on

A Dalamud plugin cannot grant in-game items — that would be cheating and a ToS problem. So
rewards are **plugin-side cosmetics and meta**, and the good news is that several already have
working systems in this codebase:

| Reward | Reuses |
|---|---|
| Window background appearances | the existing background-appearance modal |
| Completion fanfares / alternate cues | `SoundService`, `GameSound`, `/tchallenges sfx` |
| Fly-text styles | `FlyTextService` |
| Progress-toast styles | `ProgressToast` |
| Accent colours / themes | PanacheUI theming |
| Alternate icon sets | `MainWindow.Ico` |
| Titles / flair shown in-plugin | new, small |
| Prestige quest unlocks | new |

Rewards that reuse an existing system are nearly free to build — that is the list to start from.

**One to think carefully about: quest rerolls.** Spending Tokens to reroll a route you cannot do
is attractive but it directly undercuts the §8 hardest-bracket rule. Flagged, not decided.

---

<!-- SECTION:server -->
## 12. Server architecture

**Recommendation: Cloudflare Workers + D1.** Trist has already shipped a Cloudflare Worker in
this project for the Discord suggestion relay — see
[`Discord Suggestions Setup.md`](Discord%20Suggestions%20Setup.md) — so the deployment path,
account, and tooling are known-good rather than a new dependency.

Free tier covers this scale comfortably; HTTPS and a custom domain are included; there is no
server to maintain; and one Worker can serve both the plugin API and the public web view.

### Division of labour

| Concern | Where | Why |
|---|---|---|
| Quest definitions, archive, `active.json` | **Public GitHub repo** | Read-only broadcast, free CDN, sync path already built |
| Account rows, Token ledger, review queue | **Cloudflare Worker + D1** | Needs authenticated writes |
| Lodestone verification scrape | **Worker (scheduled)** | Server-side, no client involvement |

### Storage shape

Store an **append-only event ledger, not a running total**:

```
(identityHash, instanceId, tokens, awardedAt, route, bracket, evidence, trust)
```

Lifetime and Balance are materialized views over it. This one decision gives resync, admin
audit, and retroactive invalidation — replay the ledger minus the bad rows — from a single
structure. It is deliberately the same shape as the existing `completions-permanent.json`
append-only design.

---

<!-- SECTION:anticheat -->
## 13. Anti-cheat posture

Trist's stance, recorded verbatim in spirit: cheating is *"not a super serious problem."*
Tiers 3 and 4 from the earlier threat model (recompiled DLL, memory editing) are explicitly
**out of scope** — they are unclosable anyway, since the client runs on the attacker's machine.
Only tiers 1 and 2 matter.

**Governing principle, Trist's words:** *"Assume that it's a software bug rather than nefarious
player activity."* And on exploits: *"Shame on me for allowing them to get through — no need to
be harsh on the player for exploiting something easily available."*

### The one structural rule worth stating publicly

**The client never sends a Token value.** It sends "instance X completed"; the server looks up
what that is worth. This alone kills the entire casual-cheat tier.

### Everything else lives in `SECURITY.local.md`

> 🔒 **Detection thresholds, the identity/HMAC scheme, and the secrets inventory are in
> `SECURITY.local.md`, which is gitignored and never published.**

Publishing exact thresholds is a roadmap for evading them — an evader who knows the timing
variance cutoff simply adds jitter. The shape is public; the numbers are not.

In outline: a small set of hard rejects (idempotency, unknown quest id, an absurd-single-award
cap), and everything else as a **soft signal routed to a review queue** that never auto-bans.

### Local storage

Local Token data should be stored **hashed/obfuscated** to stop casual editing, with the same
honesty `BanService` already states about itself: **this is friction, not security.** An
attacker who edits the file can recompute the hash, and an HMAC key shipped in the DLL is
extractable. It is a tripwire for detecting edits, and edits detected should default to
**benefit of the doubt** — log, do not punish.

### Bans

Must be **easily reversible.** Accidents are assumed. The existing `BanService` is already
fail-open and cache-backed, which is the right posture to inherit.

### Offline completions — dropped

Raised earlier as a soft spot; Trist's response: *"it's odd that this could happen when the
plugin itself is used while playing an online-only MMO."* Correct. **Not a real case.** The
client should still queue and retry on transient failure, but no special trust handling.

---

<!-- SECTION:review -->
## 14. The review queue

Trist wants anomalies *"logged and stored in a 'review cheating behavior' location for me to
work on/review individually"* — and, crucially, **bugs fixed rather than ignored.**

So the queue is a **bug-detection tool first and an enforcement tool second.** A table in D1
plus a simple admin web view:

| Column | Purpose |
|---|---|
| `identityHash` | who (searchable from a support request) |
| `signal` | which check fired |
| `observed` / `expected` | the numbers |
| `instanceId`, `route`, `bracket` | reproduction context |
| `pluginVersion` | **the field that turns a report into a bug fix** |
| `status` | new / bug / cheat / ignored |

If one signal spikes across many players on one plugin version, that is a bug, and the version
column is what makes it visible at a glance.

---

<!-- SECTION:decisions -->
## 15. Decision log

| # | Decision | Date |
|---|---|---|
| 1 | Currency is **Challenge Tokens** ("Tokens") | 2026-08-26 |
| 2 | Track **Lifetime** and **Balance** separately; score never decreases | 2026-08-26 |
| 3 | Quest **definition** and **instance** are separate concepts | 2026-08-26 |
| 4 | Definitions ship via the **public Sync repo**; Tokens via a **server** | 2026-08-26 |
| 5 | **No background sync timer.** Load + lazy-on-open + manual button | 2026-08-26 |
| 6 | **Past-due completions are accepted**, not rejected | 2026-08-26 |
| 7 | Tier × Route × Bracket; **any one route closes the tier** | 2026-08-26 |
| 8 | Client offers only the **hardest bracket the player qualifies for** | 2026-08-26 |
| 9 | Zone/gear/emote/mount conditions are **guidance only**, never objectives | 2026-08-26 |
| 10 | **"Reach level N" is ruled out** as an objective | 2026-08-26 |
| 11 | Plugin is **fully functional with only a download**; accounts are optional | 2026-08-26 |
| 12 | Three account tiers: Local / Anonymous-default / Lodestone-linked | 2026-08-26 |
| 13 | **Drop IP** from identity — recommended, pending Trist's confirmation | 2026-08-26 |
| 14 | Identity is a **local random secret**; name+world is a label | 2026-08-26 |
| 15 | Store **HMAC(pepper, name@world)**, never plaintext at rest | 2026-08-26 |
| 16 | Lodestone link targets the **numeric character ID**, not the name | 2026-08-26 |
| 17 | Only threat tiers **1 and 2** are in scope | 2026-08-26 |
| 18 | Anomalies go to a **review queue**; assume bug before malice | 2026-08-26 |
| 19 | **Cloudflare Workers + D1**, reusing the existing Worker experience | 2026-08-26 |
| 20 | Generation is **backward-chaining from a capstone** | 2026-08-26 |
| 21 | Supersedes **Q10** ("no points, ranks, or badges") | 2026-08-26 |
| 22 | **Breadcrumbs are batched** onto the completion call, never sent per-step | 2026-08-26 |
| 23 | **Per-row salt AND server-side pepper** — salt adds little, but it is cheap | 2026-08-26 |
| 24 | **We maintain our own curated raw-materials list**, worked off permanently and extended rather than regenerated. Contents to be defined with Trist | 2026-08-26 |

Everything not yet built is inventoried in [`Tokens Build Backlog.md`](Tokens%20Build%20Backlog.md).

---

<!-- SECTION:open -->
## 16. Open items

Carried into [`../research/OPEN_QUESTIONS.md`](../research/OPEN_QUESTIONS.md) as Q11–Q16.

**Blocking the generator:**
- **Are mob drop tables absent from the client sheets?** (§9) Decides whether Hunt routes can
  chain into Craft routes at all. Highest-priority verification.
- Does `MonsterNote` carry a usable mob → zone → count mapping?

**Needs a working session in-game:**
- Confirm `ItemAdded` + `ICondition` provenance actually distinguishes gathered / crafted /
  bought in practice. APIs are confirmed present; **behaviour is not yet observed.**

**Design, deferred by Trist:**
- Full quest step structure ("we'll go over what the quest structure should be later")
- Bracket boundaries and per-expansion coverage ("we'll discuss this later, remind me")
- Reroll pricing vs. the hardest-bracket rule (§11)
- Claim-transfer threshold for silent merge vs. Lodestone gate (§4)

**Confirmation needed from Trist:**
- Dropping IP from the identity model (§3) — this is a change to his stated proposal.
