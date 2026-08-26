# docs/ — Documentation Index

Every document in `docs/` gets a row here. Add the row **in the same session** the doc is
created — an unindexed doc is an invisible doc.

| Document | Covers | Last updated |
|----------|--------|--------------|
| [How To Versioning](How%20To%20Versioning.md) | **Generic — safe to copy into any project.** The `A.B.C.D` scheme, the decimal-fraction rule for `B`, how to build a weighted milestone table, single-source-of-truth guidance, and the per-change checklist. Contains nothing project-specific. | 2026-08-22 |
| [Road to 1.0](Road%20to%201.0.md) | **This plugin only.** The agreed 1.0 scope decisions, the milestone table, current version, and the files carrying a manual version copy. | 2026-08-22 |
| [Discord Suggestions Setup](Discord%20Suggestions%20Setup.md) | How the in-plugin Suggest button reaches Discord, where the endpoint is stored and why it is gitignored, the raw-webhook vs Cloudflare-Worker security tradeoff (with Worker source), exactly what data is sent, and the abuse limits | 2026-08-22 |
| [Challenge Tokens and Quests](Challenge%20Tokens%20and%20Quests.md) | **BRAINSTORM — nothing built.** Design record for the Challenge Tokens currency and seeded Hourly/Daily/Weekly randomized quests: the three account tiers, why identity is a local secret rather than name+world+IP, Lodestone linking, quest distribution over the existing Sync repo, measured sync cost, tier×route×bracket structure, backward-chaining generation, the confirmed detector list, token spending, Cloudflare Workers+D1, and the deliberately relaxed anti-cheat posture. **Supersedes Q10.** | 2026-08-26 |

---

## Conventions

- One topic per file. Split rather than let a file sprawl.
- Any doc that will exceed ~500 lines gets a **SECTION INDEX** at the top plus
  `<!-- SECTION:anchor -->` comments before each heading. Navigate by Grep-ing the anchor,
  never by line number.
- Any doc that regularly exceeds ~1,000 lines gets designated **always-agent** in
  [`../CLAUDE.md`](../CLAUDE.md) §6 — from then on it is queried with an Explore agent and a
  specific question, never read directly.

## Update policy (match the method to the size of the change, not the size of the doc)

| Change size | Method |
|-------------|--------|
| One row, one flag, one status | `Edit` directly — no full read needed |
| One section | Grep the anchor → `Read` that section → `Edit` |
| 10+ entries or a reorganization | Explore/general agent |
| Always-agent docs | Always an agent |
