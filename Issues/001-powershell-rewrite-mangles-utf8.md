# 001: Bulk-editing .cs files with PowerShell corrupted them

**Status:** ✅ FIXED
**Date:** 2026-08-22
**Keywords:** powershell, encoding, utf-8, mojibake, Get-Content, Set-Content, bulk edit, source corruption

## Symptom

After a bulk find/replace across four source files, the files contained mojibake where
non-ASCII characters had been: `—` became `â€"`, `§` became `Â§`, `＋` became `ï¼‹`. One
comment's `//` had also been mangled into `\ `, which was a hard compile error.

The damage was invisible in the build output at first because the corrupted characters were
mostly inside comments — but several were inside **UI string literals**, which would have
shipped visible garbage into the plugin window.

## Root Cause

The rewrite used:

```powershell
(Get-Content $file -Raw) -replace 'a','b' | Set-Content $file -Encoding utf8
```

Windows PowerShell 5.1's `Get-Content` reads a file with **no BOM** using the system ANSI code
page, not UTF-8. These source files are UTF-8 without BOM. So each multi-byte UTF-8 character
was read as two or three separate Latin-1 characters, and `Set-Content -Encoding utf8` then
faithfully re-encoded that misreading — classic double-encoding. The round trip is lossy in one
direction only, so nothing errors; the file just quietly becomes wrong.

## Attempts

| Date | Attempt | Result |
|------|---------|--------|
| 2026-08-22 | Targeted `Edit` to fix the mangled comment line | Failed — `old_string` no longer matched, because the on-disk text was mojibake |
| 2026-08-22 | Measure the damage with a PowerShell regex containing the mojibake literals | Failed — passing those bytes back through the shell caused a parser error |
| 2026-08-22 | `git checkout --` the four affected files, then re-apply every change with the `Edit` tool | Worked. Verified afterwards with a byte scan for the `C3 A2 E2` double-encoding signature: 0 files affected |

## Resolution / Lesson

**Never use `Get-Content`/`Set-Content` (or any PowerShell text round trip) to rewrite source
files.** Use the `Edit` tool, which handles encoding correctly and fails loudly on a mismatch
instead of silently corrupting.

If a bulk rename genuinely is needed across many files, either:
- do it as a series of `Edit` calls (`replace_all: true` handles repeats within a file), or
- if PowerShell is unavoidable, read and write explicitly:
  `[IO.File]::ReadAllText($p, [Text.UTF8Encoding]::new($false))` and
  `[IO.File]::WriteAllText($p, $s, [Text.UTF8Encoding]::new($false))`.

**Recovery is cheap if you commit often.** The fix was `git checkout --` on exactly the affected
files, precisely because the previous commit was clean and recent. That is the practical argument
for the commit-after-every-major-change rule.

### It happened a second time

On the same day, within an hour of writing this file, the same mistake was repeated on
`TieriChallengesFFXIV.csproj` and `TieriChallengesFFXIV.json` during a version bump — a
"trivial" two-token replacement that felt too small to bother with the Edit tool. It mojibaked
the manifest's user-facing `Description` field, which ships to real users.

**The rule has no size exemption.** "It's only changing a version number" is exactly the framing
that caused the repeat. Any PowerShell text round trip over a source or config file corrupts it,
regardless of how small the edit is.

**Verification command** — byte scan for the double-encoding signature, worth running after any
bulk source edit. Note it will always flag *this* file, because the mojibake examples above are
quoted here deliberately; that one hit is expected and is not damage.

```powershell
Get-ChildItem src\*.cs | ForEach-Object {
    $b = [IO.File]::ReadAllBytes($_.FullName)
    for ($i = 0; $i -lt $b.Length - 2; $i++) {
        if ($b[$i] -eq 0xC3 -and $b[$i+1] -eq 0xA2 -and $b[$i+2] -eq 0xE2) { $_.Name; break }
    }
}
```
