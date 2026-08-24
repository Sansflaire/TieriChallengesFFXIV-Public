# Issues/ — one file per issue

**Do not pre-read these files at session start.** Open one only when the task at hand
touches a similar problem. The indexes ([`../BROKEN.md`](../BROKEN.md) and
[`../KNOWN-ISSUES.md`](../KNOWN-ISSUES.md)) are what you scan; these are what you open.

## Naming

`Issues/<ID>-<short-slug>.md` — e.g. `Issues/003-config-deserializes-empty.md`

IDs are zero-padded, sequential, never reused. Resolved issues with no remaining
diagnostic value move to [`archive/`](archive/) (the ID and the index row stay).

## When to create one

When the bug: (a) required more than one attempt to resolve, (b) could plausibly recur, or
(c) produced a non-obvious lesson. A trivial single-attempt fix caught immediately doesn't
need a file — a one-sentence inline note is enough.

## Format

```markdown
# <ID>: <Title>
**Status:** ✅ FIXED | ⚠️ ACTIVE | 🔄 INVESTIGATING
**Date:** YYYY-MM-DD
**Keywords:** comma, separated, terms

## Symptom
What was observed.

## Root Cause
What actually caused it.

## Attempts
| Date | Attempt | Result |
|------|---------|--------|
| ... | ... | ... |

## Resolution / Lesson
What fixed it and what to never do again.
```
