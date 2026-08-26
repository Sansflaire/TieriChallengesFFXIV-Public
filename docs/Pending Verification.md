# Pending Verification — things Trist still needs to test, check, or decide

**This is the standing reminder list.** Claude re-reads it and raises anything still `⬜ TODO` at
the start of any session touching Challenge Tokens or randomized quests. Nothing leaves this file
until it is genuinely done — move it to **Completed** with the date and the answer, never delete.

Created 2026-08-26.

---

## ⬜ Needs a live game session

### V1 — Inventory provenance (Q13) · CRITICAL · **tool is built and waiting**
**Does `ItemAdded` + `ICondition` reveal HOW an item arrived?**

If gather, craft and buy produce the same condition-flag set, "gather 20 copper ore" is
satisfiable by buying it, and the Gather/Craft routes need a different detector. This blocks
the whole quest generator.

**How:** `/tchallenges probe` → for each row, press Record, do that one action in game, press
Stop → **Write Report**. Reports land in
`pluginConfigs/TieriChallengesFFXIV/probe/`. Tell Claude when done; it reads them from disk.

Critical rows: **gather**, **craft**, **buy**. The other three (mob-loot, retainer, market) are
useful but not blocking.

> Status: probe shipped in the dev build 2026-08-26 and is loaded in-game. Not yet run.

### V2 — Kill attribution end-to-end
Q7 already approved the `ActionEffectHandler.Receive` hook (DamageMeter's pattern) and the
recorded dead end rules out object-table HP watching. **The design is settled; what has not
happened is a live run** confirming a specific `BNpcName` and a quantity count correctly with
the hook in place.

### V3 — MonsterNote schema
The probe's sheet census dumps `MonsterNote` + `MonsterNoteTarget` columns and sample rows.
Runs automatically as part of **Write Report** — no separate action, but nobody has read the
output yet. Needed to confirm mob → zone → count → class/level actually resolves.

### V4 — The catalogue cache is not stale · **highest regression risk of the review pass**
`ChallengeCatalog.Combined` and `.FindCustom` were memoised on `Configuration.StateVersion` in
0.84.38.1. Every mutation path was checked by grep and every one bumps it — but a path that does
not would show as **a list that silently stops updating**, which looks like nothing happening
rather than like an error.

**How (about a minute, dev build):** with the window open, do each of these and confirm the list
reacts immediately — create a challenge in the Creator, edit one, delete one, change Sort mode,
run a Sync, and complete something. Any one that does not refresh is the bug.

### V5 — Objective progress reads correctly in all three places
The row, the objective sheet and the map pin each used to read the wrong store (BROKEN.md 009).
All three now go through `ChallengeTracker.SatisfiedStops`.

**How:** part-finish a multi-stop adventure, then check (a) the row shows the right *x of y* **from
another zone**, (b) the objective sheet ticks the same stops — including for a `SessionOnly` one,
which previously always read 0 of N, and (c) the map pin points at the **next unfinished** stop
rather than the first.

### V6 — Audio settings changes
Two fixes that only ear-testing confirms: the per-cue **Play** buttons in Settings → Sound now
sound **while muted** (they were silent, beside a comment saying they should not be), and the
volume slider now applies to the **Zone has challenges** cue, which it previously skipped entirely.
At volume 1.0 nothing should sound different from before.

### V7 — A built-in background survived the JPEG rename
Confirmed only that the four backgrounds *look* fine. Not yet confirmed that a config which had
one **selected as `.png`** followed the rename rather than coming back blank — that is
`BackgroundLibrary.ResolveRenamed`, and it only runs for someone who had one selected before
0.84.38.2. Check the window still shows the background you had chosen.

---

## ⬜ Needs a decision from Trist

### D1 — Public repo history
The design doc, including the original detailed §13 anti-cheat thresholds, **is already on
`origin/main`** — pushed as a side effect of a concurrent session's push on 2026-08-26 before it
could be thinned. The current version points at the gitignored `SECURITY.local.md`, but git
history retains the original.

Options: (a) leave it — design commentary, no credentials; (b) rewrite public history
(disruptive, requires force-push). **Recommendation: (a).**

### D2 — Quest step structure (Q14)
Deferred by Trist. Note the new constraint from the drop-table finding: **Hunt cannot chain into
Craft**, so multi-part material flow must run Gather → Craft or vendor → Craft.

### D3 — Bracket boundaries and per-expansion coverage (Q15)
Deferred by Trist.

### D4 — Reroll pricing vs. the hardest-bracket rule
Spending Tokens to reroll a route undercuts the anti-cheese rule. Undecided.

### D5 — Claim-transfer threshold
Below how many Tokens does a name-match merge happen silently, and above which does it require
Lodestone verification?

---

## ⬜ Infrastructure not yet started

> **The full build inventory moved to [`Tokens Build Backlog.md`](Tokens%20Build%20Backlog.md)**
> (created 2026-08-26). The items below are kept because they carry detail the backlog references
> — but the backlog is the authoritative list of what does not exist. Add new build items THERE,
> not here. This file stays focused on what needs **testing or deciding**, not building.

### I1 — Cloudflare Worker + D1 for the Token ledger
A `worker/` directory with wrangler state already exists in this repo (used for the Discord
suggestion relay), so the account and tooling are in place. The Token API, D1 schema, and the
review-queue table are all unbuilt.

### I2 — Lodestone verification scraper
Bio-token issue → fetch → confirm → mark verified. Target the **numeric Lodestone character ID**,
not the name. Unbuilt.

### I3 — Secrets provisioning
`TOKEN_PEPPER`, `ADMIN_KEY`, `LODESTONE_UA` — see `SECURITY.local.md` §6.

**No pepper exists yet** (verified 2026-08-26 — nothing in the repo, the worker, or
`SECRETS.local.md` mentions one). When it is generated, back it up to all three places Trist
asked for, in this order:

1. **Private vault repo** (`Sansflaire/TieriChallengesFFXIV`) — the durable copy
2. **Local PC**, alongside `C:\Users\trist\Documents\SansflaireCertificate\`
3. **`SECRETS.local.md`** in this project (gitignored) — the nearby working copy

Then `wrangler secret put TOKEN_PEPPER`. **Losing it makes every stored `identityHash`
unlookupable** — name-based support requests stop working permanently.

### I4 — Drop-data extraction pipeline
Curated `drops.json` built at dev time from community databases (Garland Tools et al.), published
to the Sync repo. **Never scraped at runtime.** Must encode the exclusion rules in
`docs/Challenge Tokens and Quests.md` §9b — savage/extreme drops from the current or previous
expansion, and beast-tribe-currency items — applied across the capstone's **entire** ingredient
tree, not just its top level. The expansion window must be data, not a constant. Unbuilt.

---

## ✅ Completed

| # | Item | Answer | Date |
|---|---|---|---|
| — | Are mob drop tables in the client sheets? (Q11) | **No.** No `DropList`/`LootTable`/`BNpcDrop`/`MonsterDrop` type exists in `Lumina.Excel.dll`. Loot is server-side. Hunt routes are kill-count only and cannot chain into Craft. | 2026-08-26 |
| — | Does `MonsterNote` exist for Hunt routes? (Q12, partial) | **Yes** — `MonsterNote` and `MonsterNoteTarget` both present and bind normally. Schema still unread (see V3). | 2026-08-26 |
| — | Are `IGameInventory` / `ICondition` available and injected? | **Yes, both** — already injected in `Plugin.cs`, and `InventoryWatcher` already consumes all six inventory events. (An earlier claim that they were missing was wrong.) | 2026-08-26 |
| — | Is `GilShopItem` reachable via `GetExcelSheet<T>()`? | **No** — it is a subrow sheet (`IExcelSubrow<T>`). Needs `GetSubrowExcelSheet<T>()`. | 2026-08-26 |
| — | Identity model: name+world+IP? | **No — dropped.** Identity is a locally-generated 128-bit secret; name+world is a label. Trist agreed 2026-08-26. Removes the rename problem entirely. | 2026-08-26 |
| — | Do the JPEG q94 backgrounds (0.84.38.2) look acceptable in game? | **Yes** — Trist confirmed by eye on a local display. The PNG→JPEG conversion stands; no re-encode needed. Masters remain recoverable from git history before `2d36391` if that ever changes. | 2026-08-26 |
