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

### I1 — Cloudflare Worker + D1 for the Token ledger
A `worker/` directory with wrangler state already exists in this repo (used for the Discord
suggestion relay), so the account and tooling are in place. The Token API, D1 schema, and the
review-queue table are all unbuilt.

### I2 — Lodestone verification scraper
Bio-token issue → fetch → confirm → mark verified. Target the **numeric Lodestone character ID**,
not the name. Unbuilt.

### I3 — Secrets provisioning
`TOKEN_PEPPER`, `ADMIN_KEY`, `LODESTONE_UA` — see `SECURITY.local.md` §6. None created yet.

---

## ✅ Completed

| # | Item | Answer | Date |
|---|---|---|---|
| — | Are mob drop tables in the client sheets? (Q11) | **No.** No `DropList`/`LootTable`/`BNpcDrop`/`MonsterDrop` type exists in `Lumina.Excel.dll`. Loot is server-side. Hunt routes are kill-count only and cannot chain into Craft. | 2026-08-26 |
| — | Does `MonsterNote` exist for Hunt routes? (Q12, partial) | **Yes** — `MonsterNote` and `MonsterNoteTarget` both present and bind normally. Schema still unread (see V3). | 2026-08-26 |
| — | Are `IGameInventory` / `ICondition` available and injected? | **Yes, both** — already injected in `Plugin.cs`, and `InventoryWatcher` already consumes all six inventory events. (An earlier claim that they were missing was wrong.) | 2026-08-26 |
| — | Is `GilShopItem` reachable via `GetExcelSheet<T>()`? | **No** — it is a subrow sheet (`IExcelSubrow<T>`). Needs `GetSubrowExcelSheet<T>()`. | 2026-08-26 |
| — | Identity model: name+world+IP? | **No — dropped.** Identity is a locally-generated 128-bit secret; name+world is a label. Trist agreed 2026-08-26. Removes the rename problem entirely. | 2026-08-26 |
