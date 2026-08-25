# 005 — Every icon was a grey placeholder in the public build

**Status:** Fixed 2026-08-25 (0.81.29.0)
**Keywords:** icons, panacheicons, placeholder, build-public, payload, resolveiconsfolder, works-on-my-machine, assembly.location, shadow copy
**Reached real users:** yes — every public release that shipped the icon UI

---

## Symptom

In the public build, every bundled icon rendered as a grey swatch with a thin border: the
completion checkbox on every challenge row, the hint button, the close and lock chrome, the
"you are here" pin, the five-pip difficulty meter, the category-complete mark, and every
dropdown-menu glyph.

Not reported as "icons are broken" — a placeholder swatch reads as an intentional empty box,
so the UI looked *designed that way*, just worse.

On the dev machine everything was correct, in both Debug and public-preview mode.

---

## Root cause

Two independent gaps that only combine outside this machine.

**1. The zip never contained the icons.** `scripts/build-public.ps1` copied the plugin DLL, the
manifest, `PanacheUI.dll`, both SkiaSharp halves, `sounds/`, and `backgrounds/`. It never copied
`Icons/`. Confirmed against the shipped artifact:

```
$ unzip -l dist/TieriChallengesFFXIV-0.81.28.0.zip
   backgrounds/... sounds/... libSkiaSharp.dll PanacheUI.dll SkiaSharp.dll
   TieriChallengesFFXIV.dll TieriChallengesFFXIV.json
```

Eleven entries, no `Icons/`.

**2. The framework's search only ever succeeds on a dev machine.**
`PanacheIcons.ResolveIconsFolder` tries, in order:

1. `%APPDATA%\XIVLauncher\devPlugins\PanacheUI\Icons` — the well-known dev path
2. `Icons` beside `Assembly.Location`
3. `..\PanacheUI\Icons` beside `Assembly.Location`

For someone who installed through Dalamud, (1) does not exist, and (2) and (3) could not
succeed because of gap 1. `Get` then returns null for every id and `PUI.Icon` degrades to its
placeholder — by design, and silently, which is correct behaviour for a missing icon and
exactly what made this invisible.

**Why it survived testing.** Strategy (1) is checked *first* and always hits here. Public-preview
mode (`/tchallenges preview`) faithfully reproduces the public *UI* but runs from the dev folder,
so it could never reproduce the public *asset layout*. The one thing preview mode does not
preview is where files are.

---

## Fix

- `build-public.ps1` copies `PanacheUI\Icons\*.png` into the payload and **fails the build** if
  the folder is missing or empty. A warning would not have been enough: the failure mode looks
  like a design choice, so nobody goes looking for a warning.
- `TieriChallengesFFXIV.csproj` gained a `CopyPanacheIcons` target so `$(TargetDir)` and the dev
  deploy are self-contained too.
- `PanacheIcons.FolderOverride` (new) takes precedence over all three automatic strategies when
  set and existing. Setting it clears the id→bitmap cache, because a miss is cached as null and
  would otherwise outlive the fix.
- `Plugin.TrySetIconFolder` points it at `PluginInterface.AssemblyLocation.Directory\Icons`.
  `Assembly.Location` alone is not trustworthy — Dalamud may shadow-copy an assembly to a temp
  directory — so the consumer, which knows its own install path for certain, tells the framework
  rather than letting the framework guess.

Verified: `dist/TieriChallengesFFXIV.zip` now holds 89 entries, 78 of them `Icons/*.png`.

---

## Lessons

**A fallback chain whose first branch always succeeds locally is untested code.** Strategies 2
and 3 in `ResolveIconsFolder` had never once executed on this machine. They were written as
robustness and were in practice unverified guesses; the shipping path depended entirely on them.
When you write a "try A, else B, else C" resolver, ask which branch runs *for a user* — and if
the answer is "not the one I test", test that branch deliberately.

**Ship-list bugs do not show up in a build log.** Both configurations compiled warning-free, the
dev-marker guard passed, the manifest matched, the signature was valid. Every gate the release
script had was green, because none of them asked "is the artifact *complete*". The manifest
check proves the zip is *installable*, not that it *works*. When adding an asset class to the
plugin — sounds, backgrounds, icons — add a packaging assertion in the same commit.

**Degrading gracefully can hide a total failure.** `PUI.Icon` falling back to a placeholder
instead of throwing is right for one missing id and wrong as the outcome for all 78. A
best-effort path that silently absorbs a systemic failure needs a loud signal somewhere; a
one-line "icons folder not found at X" warning on first miss would have surfaced this instantly.

---

## Related

- Cross-plugin [`../../BROKEN.md`](../../BROKEN.md) §1 — each plugin's `PluginLoadContext`
  resolves DLLs only from its own directory, which is why `PanacheUI.dll` is copied in at all.
  Same class of problem, assets rather than assemblies.
- [002](002-crlf-hash-mismatch-rejected-every-challenge.md) — also shipped green and also only
  failed for real users.
