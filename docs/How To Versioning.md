# How To Versioning

A reusable `A.B.C.D` versioning scheme. **This document is generic — it is written to be copied
into any project unchanged.** Nothing here is specific to one codebase, one language, or one
platform.

Each project that adopts it keeps its own **milestone document** alongside this one. That is the
only project-specific piece: this file defines the rules, the milestone document supplies the
numbers.

---

## 1. The scheme

```
v0.59.1.0
  │ │  │ └── D  minor update counter
  │ │  └──── C  major update counter
  │ └─────── B  progress toward the next full release
  └───────── A  channel: 0 = beta, 1 = released
```

| Digit | Meaning | Range | Changes when |
|-------|---------|-------|--------------|
| **A** | Release channel | `0` beta, `1` released | The project ships a full public release, or a later cycle begins |
| **B** | Progress toward the next full release | see §2 | A milestone's status changes |
| **C** | Major update counter | `1`+, unbounded | A feature, behaviour, or data-format change |
| **D** | Minor update counter | `0`+, unbounded | A fix, tweak, or polish pass. **Resets to 0 when C increments** |

---

## 2. B is a decimal fraction with the `0.` stripped

This is the one rule people get wrong. **Read `B` as the digits after a decimal point.**

| Written | Reads as | Means |
|---------|----------|-------|
| `0.1.0.0` | 0.1 | 10% |
| `0.2.0.0` | 0.2 | 20% |
| `0.5.0.0` | 0.5 | **50%** |
| `0.25.0.0` | 0.25 | 25% |
| `0.43.0.0` | 0.43 | 43% |
| `0.59.0.0` | 0.59 | 59% |
| `0.9.0.0` | 0.9 | 90% |

**Trailing zeros in B are invalid.** `0.5` and `0.50` are the same number, so only the short form
is written.

| Valid | Invalid | Why |
|-------|---------|-----|
| `1.2.15.520` | `1.20.15.520` | `20` is `2` with a redundant zero |
| `0.5.3.1` | `0.50.3.1` | same |
| `0.59.1.0` | — | `59` has no trailing zero, so it stands |

**A and B never carry padding zeros. C and D are ordinary counters** and may contain any digits,
zeros included — `15`, `100`, `520` are all fine.

### Precision, and the one gap

- One digit gives you tens: `1`–`9` → 10%–90%.
- Two digits give you whole percent: `10`–`99` → 10%–99%, minus the multiples of ten, which are
  written as one digit.
- **`0` means 0%** — nothing done yet.
- **100% is not representable in B, by design.** Finishing everything is what flips A, so the
  version becomes `1.0.0.0` rather than `0.100.x.x`.
- **Known gap: values below 10% that are not 0 cannot be written.** `0.05` would need a leading
  zero inside B, which the no-padding rule forbids and which most version parsers would collapse
  to `5` (= 50%) anyway. In practice a project at 3% rounds to `0` or `1`. Round honestly and
  move on; this only bites in the first few days of a project.

---

## 3. Which digit do I bump?

Every change bumps something. Nothing ships unversioned.

**Bump C (major), and reset D to 0**, when the change:
- Adds or removes a feature
- Changes behaviour a user would notice
- Changes anything persisted — file formats, schemas, stored identifiers
- Adds or removes a developer-facing tool

**Bump D (minor)** when the change:
- Fixes a defect without altering intended behaviour
- Adjusts layout, wording, colour, or spacing
- Refactors internals with no user-visible effect
- Updates documentation

**Recompute B independently** whenever a milestone's status moves. B is not tied to C or D — one
commit can move B *and* C.

**When unsure between C and D, ask:** *would a user notice, or could this lose someone's data?*
Either yes means C.

---

## 4. Making B computable: the milestone document

B is only meaningful if "full release" is defined. Without that it is a mood, and a mood that
only ever drifts upward.

Each project keeps a milestone document containing a table of everything the next full release
requires, with a **weight** per milestone. **The weights must total exactly 100.**

> **B = the sum of the weights of every completed milestone.**

### Template

```markdown
| # | Milestone | Weight | Status |
|---|-----------|--------|--------|
| 1 | <a coherent, shippable chunk of work>       | 8  | ✅ done |
| 2 | <another>                                   | 10 | 🔶 half (5 of 10 counted) |
| 3 | <another>                                   | 6  | ⬜ not started |
| … | …                                           | …  | … |
|   | **Completed total → B**                     | ** ** | |
|   | **Remaining**                               | ** ** | |
```

### Rules for the table

- **Weights total exactly 100.** If you add a milestone, take the weight from somewhere or
  accept that B drops.
- **Weight by effort and risk, not by excitement.** A dull release pipeline that takes three days
  outweighs a fun feature that takes three hours.
- **Milestones are shippable chunks**, not tasks. "Search and filter in the list" is a milestone;
  "add a text box" is not.
- **Partial credit is allowed** for something genuinely half-built. Round to a whole number and
  show the working in the Status column.
- **B is allowed to go down.** Discovering new required work is normal and the number should say
  so. Never pad weights to protect it — a version that lies is worse than one that disappoints.
- **Re-derive B from the table; never edit B directly.** The table is the source, B is the
  output.

---

## 5. Where the version lives

Pick **one** source of truth and derive everything else from it. Every hand-maintained copy is a
place for drift to hide.

The general pattern:

1. **One declaration** in the build configuration (`.csproj`, `package.json`, `pyproject.toml`,
   `Cargo.toml`, a `VERSION` file — whatever your stack builds from).
2. **Runtime code reads it back from the built artifact** — assembly metadata, package metadata,
   an embedded constant generated at build time. Do **not** re-declare the literal in source; a
   second literal is a second thing to forget.
3. **Anything else that must state a version gets a checklist entry**, because it will not
   follow automatically.

Keep a table like this in your project's milestone document, so the manual copies are visible:

| File | Field | Automatic? |
|------|-------|-----------|
| build config | version declaration | source of truth |
| runtime code | reads built artifact metadata | ✅ |
| UI / logs / about box | reads the runtime value | ✅ |
| package or plugin manifest | version field | ❌ **update by hand** |
| distribution index / release notes | version field | ❌ **update by hand** |

**Audit the manual ones before every release.** A manifest that disagrees with the binary is the
classic failure that passes every local test and fails only for real users installing it.

---

## 6. Checklist for every change

1. Make the change.
2. Decide C or D (§3) and bump it in the single source of truth.
   **Bumping C resets D to 0.**
3. If a milestone's status moved, update the table and recompute B (§4).
4. Update every file marked ❌ in the manual-copies table (§5).
5. Update the "current version" line in the milestone document.
6. Build and run whatever verification the project has. Clean, no new warnings.
7. Commit, stating the new version and which digit moved and why.

---

## 7. Worked examples

| Change | Before | After | Why |
|--------|--------|-------|-----|
| Fix a mis-positioned label | `0.59.1.0` | `0.59.1.1` | Cosmetic → D |
| Add a search feature (milestone weight 4) | `0.59.1.1` | `0.63.2.0` | Feature → C, D resets; B 59 → 63 |
| Reword the search placeholder | `0.63.2.0` | `0.63.2.1` | Wording → D |
| Change an on-disk data format | `0.63.2.1` | `0.63.3.0` | Persisted data → C |
| Finish a milestone worth 8, as a feature | `0.63.3.0` | `0.71.4.0` | Feature → C; B 63 → 71 |
| Discover 6 points of newly required work | `0.71.4.0` | `0.67.4.1` | Table grew; B honestly falls |
| Ship the full release | `0.9.9.2` | `1.0.0.0` | A flips; B restarts toward the next full release |

---

## 8. Rules that are easy to get wrong

- **`0.5.0.0` is 50%, not 5%.** B is a decimal fraction with the `0.` removed.
- **No trailing zeros in A or B.** `0.20.x.x` is invalid; write `0.2.x.x`.
- **C and D are plain counters.** Zeros are fine there — `1.2.15.520` is valid.
- **Bumping C resets D to 0.** It never carries over.
- **B can decrease.** That is the scheme working, not a mistake.
- **Never bump A casually.** `1.x` is a public promise that the thing is released.
- **The manual copies are where drift hides.** Automate what you can; check the rest.

---

*Generic document — safe to copy into any project as-is. Keep your project's milestone table and
current version in a separate, project-specific document.*
