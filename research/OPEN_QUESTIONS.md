# OPEN QUESTIONS

Unresolved questions, scratch notes, and dead ends. **Pick from here when choosing what to
work on next.**

Rules:
- Answer a question → move it to Answered with the answer and the date. Don't delete it;
  the answer is the value.
- Hit a dead end → record it under Dead Ends so nobody walks it twice.
- Discover a question mid-task → write it here immediately, then carry on with the task.

**Status:** ❓ OPEN · 🔄 INVESTIGATING · ✅ ANSWERED

---

## Open

| # | Question | Status | Raised | Blocks |
|---|----------|--------|--------|--------|
| Q2 | Where do challenge definitions live long-term? Currently BOTH: `ChallengeCatalog.BuiltIn` hardcoded, plus user-authored ones in `Configuration.CustomChallenges` via the dev-only Challenge Creator. Unresolved: does the public build ever get an authoring UI, or does it ship a fixed catalog? | 🔄 | 2026-08-22 | Public build scope |
| Q6 | Should user-authored challenges be exportable/importable (share a challenge pack)? Only matters if Q2 lands on "users author their own". | ❓ | 2026-08-22 | Q2 |
| Q7 | **Enemy challenge type (Trist's type #4) — deferred, needs a decision.** Two blockers, see Dead Ends below. (a) There is no plant/beast taxonomy in the game data, so "broadly by creature type" can only be `ModelChara.Type` (human/monster/demihuman) or name-substring matching. (b) Kill counting needs damage attribution, i.e. an `ActionEffectHandler.Receive` hook. Decide: accept the coarse taxonomy, and approve adding a hook? | ❓ | 2026-08-22 | Enemy challenges |
| Q8 | The 12 built-in challenges have no detectors, so now that manual marking is gone they can never complete. Keep them as flavour, give each a detector, or delete them? | ❓ | 2026-08-22 | Built-in catalog |
| Q3 | Does progress tracking need live game-state hooks (duty completion, achievement unlock, kill counts), or is manual check-off enough for v1? Live tracking pulls in the Brain API / FFXIVClientStructs; manual does not. | ❓ | 2026-08-22 | Data sources |
| Q4 | Should completed-challenge state be per-character or account-wide? Affects the config key shape and is painful to migrate later. | ❓ | 2026-08-22 | Config model |
| Q5 | Is there a public GitHub release pipeline for this plugin, or is it dev-only? Determines whether the `PATCH_UPDATE_PLAYBOOK.md` mandatory-release rule applies. | ❓ | 2026-08-22 | Release process |

---

## Answered

| # | Question | Answer | Answered |
|---|----------|--------|----------|
| Q1 | What is this plugin actually for? | **"FFXIV Miscellaneous Challenges"** — a plugin presenting miscellaneous in-game challenges, with a PanacheUI window. Stated by Trist on 2026-08-22. | 2026-08-22 |
| Q2 | Where do challenge definitions live long-term? | **Curated catalogue authored by Trist, built into the plugin.** The Creator stays dev-only; new challenges reach players via commit → push → build. Needs an export path from the dev pluginConfig into a source-controlled, embedded, read-only catalogue (milestone 11). | 2026-08-22 |
| Q4 | Per-character or account-wide completion? | **Account-wide**, as already built. No migration needed. | 2026-08-22 |
| Q5 | Public release pipeline, or dev-only? | **Trist's own plugin repo** via a maintained `pluginmaster.json`. No official-repo submission for 1.0, so no third-party review constraints. Milestone 15. | 2026-08-22 |
| Q6 | Export / import challenge packs? | **No** — dropped from scope. Only meaningful if players author challenges, and they don't. | 2026-08-22 |
| Q7 | Enemy challenge type — accept the coarse taxonomy and approve a hook? | **Yes to both. Required for 1.0.** `ActionEffectHandler.Receive` hook following DamageMeter's pattern; creature filtering by `ModelChara.Type`, specific `BNpcName`, and name matching, since no plant/beast taxonomy exists. Milestone 10. | 2026-08-22 |
| Q8 | What happens to the 12 detector-less built-ins? | **Replaced.** 1.0 ships ~15–20 real curated challenges that each have a working detector; the current placeholders are not shippable. Catalogue starts small and grows in point releases. Milestone 12. | 2026-08-22 |
| Q9 | How does a player find a challenge's location? | **Zone name + written hint only.** Finding the exact spot is part of the challenge. No player-facing in-world markers — the overlay stays a dev placement aid. | 2026-08-22 |
| Q10 | Any reward beyond a checkmark? | **No** — completion plus its date is the whole reward. No points, ranks, or badges. | 2026-08-22 |

---

## Dead Ends

| Approach | Why it failed | Date |
|----------|---------------|------|
| Classifying enemies as "plant-based" / "beast" from game data | No such taxonomy exists. `BNpcBase` has no family/type column; `BNpcResist` is 256 rows of unlabelled booleans with no link back to a mob. The only things that resolve are `ModelChara.Type` (1=human, 2=monster, 3=demihuman), `BNpcName` for specific creatures, and name-substring matching. LimLoToolkit hit this same wall and documented it in `MobViewerTool.cs` — "rather than invent a column, the viewer shows what genuinely resolves." Don't re-derive this. | 2026-08-22 |
| Counting kills without a hook | Watching object-table HP for a 0 transition cannot tell *who* killed it, so anything dying nearby would count. Correct attribution needs the `ActionEffectHandler.Receive` hook — the working pattern is in `DamageMeter/src/CombatTracker.cs` (`ActionEffectHandler.Addresses.Receive.Value` + `HookFromAddress`). Reuse that rather than inventing one. | 2026-08-22 |
| `IClientState.LocalContentId` for session boundaries | Does not exist on IClientState in API 15. Not needed anyway: a character switch always passes through a logout, so the logged-out→logged-in edge is exactly "one login session". | 2026-08-22 |
