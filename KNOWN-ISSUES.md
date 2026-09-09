# KNOWN-ISSUES.md — Active Issue Index

Unresolved problems. Some have workarounds; none are fixed.

This file is an **index only** — one row per issue, with a link to the detail file in
[`Issues/`](Issues/).

**Tense: present.** Once an issue is resolved *and understood*, move its row to
[`BROKEN.md`](BROKEN.md) along with the lesson.

**Analogy:** the leaky faucet. Annoying, worked around, not yet fixed.

**Status legend:** ⚠️ ACTIVE · 🔄 INVESTIGATING · 🩹 WORKAROUND IN PLACE

---

| ID | Summary | Keywords | Status | Found | Detail |
|----|---------|----------|--------|-------|--------|
| 013 | Dig site walls drew only at some camera angles — the `WorldToScreen` gate also required the point to be *in the viewport*, so any quad with a corner off-screen was discarded. Same latent bug made Challenge Creator wireframes vanish near large volumes. | WorldToScreen, inView, overlay, gradient walls, culling, AreaOverlay, surveillance | 🔄 FIX SHIPPED 0.84.42.3 — awaiting in-game confirmation | 2026-09-09 | [013](Issues/013-worldtoscreen-gate-discarded-visible-geometry.md) |
