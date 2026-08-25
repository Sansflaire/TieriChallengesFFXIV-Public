# Road to 1.0 — TieriChallengesFFXIV

This plugin's milestone table and current version. The **rules** for the version scheme live in
the generic [How To Versioning](How%20To%20Versioning.md); this document supplies the numbers.

---

## Current version

```
0.81.32.2    beta · 81% toward 1.0 · 32 major updates · 2 minor updates
```

**Repositories — three of them since the 2026-08-24 split. This table was wrong for a while;
the old two-repo description had the source in the PRIVATE repo, which is now backwards.**

| Repo | Visibility | Holds |
|------|-----------|-------|
| [Sansflaire/TieriChallengesFFXIV-Public](https://github.com/Sansflaire/TieriChallengesFFXIV-Public) | public | **The plugin SOURCE**, `pluginmaster.json`, README, signed release zips. The local dev folder has this as `origin`, so this is what a normal `git push` writes to. |
| [Sansflaire/TieriChallengesFFXIV](https://github.com/Sansflaire/TieriChallengesFFXIV) | private | **Vault — moderation data only.** `backup/bans-private.json` plus dated history. Kept as the `vault` remote. The only readable record of who is banned and why, and the only way to lift a ban, since published hashes are one-way. |
| [Sansflaire/TieriChallengesFFXIV-Sync](https://github.com/Sansflaire/TieriChallengesFFXIV-Sync) | public | Challenge data (`challenges/<guid>.json` + `master.json`) and the hashed, one-way `bans.json` the plugin and the Cloudflare relay both read. |

**Because the working folder now pushes to a PUBLIC repo,** anything sensitive must stay
gitignored (`CLAUDE.md`, `src/Secrets.props`, `SECRETS.local.md`) and the ban ledger's mirror path
must point at a vault checkout — `BanAdmin.PointsAtPublicRepo` hard-refuses the alternative.

Share this URL for users to add in `/xlsettings` → Experimental → Custom Plugin Repositories:

```
https://raw.githubusercontent.com/Sansflaire/TieriChallengesFFXIV-Public/main/pluginmaster.json
```

**Releasing** — run `scripts/build-public.ps1` (Release-only, dev-marker rejection, version match,
Sansflaire signing, zip-manifest verification), then upload `dist/TieriChallengesFFXIV.zip` — the
**stable-named** copy, because `releases/latest/download/TieriChallengesFFXIV.zip` requires that
exact asset name — and bump `AssemblyVersion` in the public repo's `pluginmaster.json` to match.

---

## What 1.0 is — scope decisions

Settled with Trist on 2026-08-22. These are decisions, not guesses; the milestone table below
follows from them.

| Decision | Answer | Consequence |
|----------|--------|-------------|
| **What the plugin is** | A **curated catalogue** authored by Trist. Players complete challenges; they do not author them. | The Challenge Creator stays dev-only permanently. Every shipped challenge needs a working detector — a challenge nothing can complete is not shippable. |
| **What ships to players** | **The PUBLIC (Release) build only. Never the dev build.** | Enforced by `scripts/build-public.ps1`, which builds Release only and aborts if developer strings are found in the artifact. Do not publish a DLL that did not come out of that script. |
| **Feedback channel** | An in-plugin **Suggest** button posting to Trist's Discord. | Endpoint lives in gitignored `src/Secrets.props`, baked into the DLL at build time. See [Discord Suggestions Setup](Discord%20Suggestions%20Setup.md) — the raw-webhook-vs-proxy decision is still open. |
| **Progress scope** | **Account-wide**, as already built. | No migration, no storage rework. Closed with zero work. |
| **Distribution** | **Trist's own plugin repo** via a `pluginmaster.json` users add in Dalamud. | No third-party review process or constraints. Needs GitHub releases, a maintained pluginmaster, and signing. |
| **Enemy challenge type** | **Required for 1.0.** | An `ActionEffectHandler.Receive` hook is in scope, following DamageMeter's proven pattern. The coarse creature filter is accepted — `ModelChara.Type`, specific `BNpcName`, and name matching — because no plant/beast taxonomy exists in the game data. |
| **Catalogue size at launch** | **Start small (~15–20), grow after launch.** | The catalogue is a living thing released in point updates, not a launch blocker. |
| **How curated challenges ship** | **Built into the plugin.** Trist authors with the dev Creator, then the new challenges reach players through commit → push → build. | Needs an export path out of the dev pluginConfig into a source-controlled catalogue that compiles into the DLL. See the note below. |
| **Discoverability** | **Zone name + written hint only.** | Finding the exact spot is part of the challenge. No player-facing in-world markers — the overlay stays a dev placement aid. Cheap, and it makes the description text load-bearing. |
| **Rewards** | **Just completion + date**, as already built. | No points, ranks, or badges. Closed with zero work. |

### Note on the authoring pipeline (milestone 11)

Trist's requirement: *"the plugin should have the challenges built in, so when I create more with
my dev version, I'd want them added as commit + push + build to the public version."*

Recommended shape, to be confirmed before building: a dev-only **Export catalogue** action writes
the authored challenges to a **JSON file committed to the repo and compiled in as an
`EmbeddedResource`**. That satisfies "built in" — the data lives inside the DLL, not on the
user's disk, and cannot be edited by players — while keeping the content diffable in git, which
a generated `.cs` file would not be. Loading is read-only at startup and merges into the
built-in list.

The alternative is generating a `.cs` file instead. Same workflow, tamper-proof in the same way,
but every content change produces an unreadable source diff. Say the word if you'd rather have
that.

---

## Milestone table

Weights total exactly 100. **B = sum of the weights of completed milestones.**

| # | Milestone | Weight | Status |
|---|-----------|--------|--------|
| 1 | Core UI — master–detail layout, category/detail panes, progress display, PanacheUI throughout | 8 | ✅ done |
| 2 | Challenge data model — typed kinds, GUID identity, sort order independent of identity | 6 | ✅ done |
| 3 | Durable completion storage — current + permanent files, atomic writes, migration that never loses data | 8 | ✅ done |
| 4 | Auto-completion tracker — condition evaluation with zone/completion gating and throttling | 8 | ✅ done |
| 5 | Trigger volumes — sphere/box, position/size/scale/yaw editing, in-world wireframe overlay (dev aid) | 8 | ✅ done |
| 6 | Challenge types — visit-any, visit-ordered, emote-at-location (+facing), mount-in-area, outfit/gear-in-zone | 10 | ✅ done |
| 7 | Completion popup — headline, number, descriptor, queueing, fade | 3 | ✅ done |
| 8 | Dev/public build split + Challenge Creator with in-place editing | 6 | ✅ done |
| 9 | Versioning scheme, version surfaced in UI, generic versioning doc | 2 | ✅ done |
| 10 | Enemy challenge type — `ActionEffectHandler.Receive` hook, kill counts, action-on-target, coarse creature filter | 8 | ⬜ not started |
| 11 | Catalogue pipeline — dev export → repo-hosted, hash-verified, synced official catalogue | 6 | ✅ done — Publish tab writes `challenges/<guid>.json` + `master.json`; plugin syncs, verifies SHA-256, and badges anything not in the master list as CUSTOM |
| 12 | Curated content — ~15–20 real challenges with working detectors and hint text good enough to find them by | 8 | ⬜ not started — the hint itself is now a first-class field on every challenge (own text box, own player-facing Hint button), so this milestone is pure authoring |
| 13 | Player-facing settings — popup duration, notification preferences | 1 | ⬜ not started (the renderer toggle, milestone 18, covers part of this) |
| 14 | Browse controls — search, filter, sort in the challenge list | 1 | ⬜ not started |
| 15 | Public release pipeline — public-only build guard, signing, staging, GitHub release, hosted `pluginmaster.json` | 8 | ✅ done — public repo live, manifest served over raw.githubusercontent, signed release published, both URLs verified anonymously |
| 16 | User-facing help and onboarding + README | 1 | ⬜ not started |
| 19 | Bug reporting — in-plugin log buffer, environment snapshot, one-click report with the log attached | 2 | ✅ done |
| 17 | Suggestion channel — in-plugin feedback to Discord, endpoint kept out of git, abuse limits | 3 | ✅ done |
| 18 | Renderer resilience — PanacheUI on/off toggle, plain-ImGui fallback, plugin survives the library being absent | 3 | ✅ done |
| | **Completed total → B** | **81** | |
| | **Remaining** | **19** | |

*Weights still total 100. Milestone 19 (2) was added, and milestone 12 was cut from 10 to 8:
with the sync pipeline in place, shipping content is now pure authoring with no code behind it,
so it is less work than when it was scoped.*

### Dropped from scope

| Was | Why it's gone |
|-----|---------------|
| Export / import challenge packs (weight 4) | Only meaningful if players author challenges. With a curated catalogue shipped inside the DLL, there is nothing for a player to export. |
| Player-facing in-world markers | Discoverability is hint text by design. The overlay stays dev-only. |
| Rewards / points / ranks | Completion and its date are the whole reward. |
| Per-character progress | Account-wide confirmed as correct. |

---

## Where the version lives

| File | Field | Automatic? |
|------|-------|-----------|
| `src/TieriChallengesFFXIV.csproj` | `<Version>` / `<AssemblyVersion>` / `<FileVersion>` | source of truth |
| `src/PluginVersion.cs` | reads the compiled assembly's metadata | ✅ |
| Window header + Dalamud load log | read `PluginVersion` | ✅ |
| `TieriChallengesFFXIV.json` (Dalamud manifest) | `"AssemblyVersion"` | ❌ **update by hand** |
| This document | Current version block | ❌ **update by hand** |
| `pluginmaster.json` (once milestone 15 exists) | version fields | ❌ **update by hand** |

The manifest matters: [`devPlugins/BROKEN.md`](../../BROKEN.md) records that API 15 validates
`AssemblyVersion` strictly inside a release zip. A mismatch is invisible during local dev-plugin
loading and produces "Failed to install plugin" for real users.
