# 002: Every published challenge was rejected on download (CRLF vs LF hash)

**Status:** ✅ FIXED
**Date:** 2026-08-23
**Keywords:** sha256, hash mismatch, crlf, lf, line endings, git autocrlf, raw.githubusercontent, sync, cdn cache

## Symptom

A real user installed the public build, pressed Sync, and got **zero challenges** — despite two
challenges being correctly published in the repo. His bug report showed `Official: 0 synced`.

Reproduced locally with a clearer symptom once a second bug was out of the way:

```
[Sync] 9f4f9b8f-…: hash mismatch — rejected.
[Sync] dead4e45-…: hash mismatch — rejected.
[Sync] Synced: 0 new, 0 updated, 2 rejected. 0 official challenge(s) available.
```

## Root Cause

**Two independent bugs stacked, which is why the first diagnosis was incomplete.**

**(a) CDN cache race — the one the user actually hit.** The publish commit landed at 07:19:42Z
and his sync ran at 07:20:03Z, 21 seconds later. `raw.githubusercontent.com` serves
`Cache-Control: max-age=300`, so his client fetched a five-minute-old copy of `master.json` that
predated the publish. Nothing was wrong with his install or the repo.

**(b) CRLF vs LF hash mismatch — the one that would have been permanent.** `ChallengeExporter`
serialised with `JsonConvert.SerializeObject(c, Formatting.Indented)`, which indents using
`Environment.NewLine` — **CRLF on Windows** — and hashed those bytes into `master.json`. But git
normalises text to **LF** on commit, so the stored blob and everything
`raw.githubusercontent.com` serves is LF. The downloaded bytes therefore could never match the
recorded SHA-256. **Every published challenge would have been rejected for every user, forever.**

The warning was on screen for the entire session and went unread:

```
warning: in the working copy of 'challenges/….json', LF will be replaced by CRLF the next time Git touches it
```

## Attempts

| Date | Attempt | Result |
|------|---------|--------|
| 2026-08-23 | Assumed the repo or the publish was broken | Wrong — the repo was correct; verified 2 files + a 2-entry master list |
| 2026-08-23 | Suspected the new version gate was blocking them | Wrong — published files had `MinPluginVersion` empty → `0.0.0.0` → always loadable |
| 2026-08-23 | Compared publish time to sync time against the CDN's `max-age` | Found bug (a). Added a query-string cache-buster |
| 2026-08-23 | Re-synced with cache-busting | Files now downloaded — and **both were rejected on hash mismatch**, exposing bug (b) |
| 2026-08-23 | Normalised to LF before hashing and writing; added `.gitattributes` with `challenges/*.json -text` | Fixed. `Synced: 2 new … 2 official challenge(s) available` |
| 2026-08-23 | Confirmed by the original reporter on the updated build | Working live |

## Resolution / Lesson

**Hash exactly the bytes that will be served, not the bytes you happened to produce.** Anything
travelling through git is line-ending-normalised in transit. If content is hash-verified and
stored in git, normalise it to LF *before* hashing, and pin it with `.gitattributes` so no
future checkout can convert it back.

**Read the git line-ending warnings.** `LF will be replaced by CRLF` was printed on every commit
in the session that introduced the bug. It is noise 99% of the time and load-bearing the 1% of
the time you are hashing the file.

**The verification worked correctly and is not the thing to loosen.** The instinct on seeing
"hash mismatch — rejected" is to relax the check. That would have shipped a system that accepts
tampered content in order to paper over a local formatting bug. The check refused content that
did not match its manifest; it simply caught our own bug before it ever caught an attacker's.

**Two bugs can hide behind one symptom.** "Zero challenges" was caused by (a) for the user and
(b) for everyone thereafter. Fixing (a) alone would have looked like success right up until the
next report, because (b) only becomes visible once the download actually happens.

**On the CDN mitigation — do not overstate it.** The query-string cache-buster is *not* proven to
work: measured on 2026-08-23, a cache-busted request one minute after a publish still returned
the pre-publish file, so `raw.githubusercontent.com` appears to cache by path and ignore the
query string. It is kept because it is free, but the real handling is honest messaging — sync
now tells the user a publish can take up to five minutes to appear rather than reporting a bare
zero that reads as a fault.
