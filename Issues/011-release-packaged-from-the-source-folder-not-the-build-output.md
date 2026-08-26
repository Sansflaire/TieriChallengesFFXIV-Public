# 011 — The release packaged icons from the shared source folder, so the subset never shipped

**Status:** fixed in 0.84.38.1 (present since icons were first bundled, 0.81.29.0)
**Keywords:** icons, PanacheIcon, subset, build-public, payload, zip size, PanacheUI, Consumer.props,
TargetDir, dead weight, 167, 3.9 MB

---

## Symptom

Every public zip since icons were first bundled carried **all 167 PanacheUI framework icons
(3,990 KB)** when the plugin renders **23 (499 KB)**. About 3.5 MB of dead weight per download,
roughly a fifth of the artifact. Nothing looked wrong: every icon the plugin used was present, and
the surplus was invisible.

## Root cause

The subsetting mechanism worked perfectly. The release script read from somewhere else.

`TieriChallengesFFXIV.csproj` declares the subset, and `PanacheUI.Consumer.props` copies exactly
those 23 PNGs into `$(TargetDir)\Icons`:

```xml
<PanacheIcon Include="3;4;5;9;15;19;25;28;32;36;46;47;51;52;60;62;71;97;109;115;121;137;141" />
```

`build-public.ps1` then packaged from the **shared framework folder** instead:

```powershell
$iconsSrc = Join-Path $env:APPDATA 'XIVLauncher\devPlugins\PanacheUI\Icons'   # all 167
```

This is the direct sibling of [005](005-icons-missing-from-every-public-zip.md), in the same file,
about the same asset, with the opposite error. 005 was "the icons are not in the zip"; the fix
added this block and pointed it at the folder that definitely had icons in it — which is the shared
one, and which is also the folder that only exists on this machine.

The comments made it harder to spot rather than easier. The csproj says the set is subsetted;
`devPlugins/CLAUDE.md` mandates subsetting and states the full set is 3.9 MB. Both were true.
Neither described what shipped.

## Fix

Package from `Split-Path $Dll -Parent`/`Icons` — the Release output beside the signed DLL, which is
where Consumer.props put the subset. The build now also fails if the packaged count equals the full
framework count, since a silent revert to the whole set is the specific regression worth catching,
and reports what it bundled: `bundled 23 of 167 icon(s), 499 KB`.

## Lessons

- **A fix for "the asset is missing" must package the same artifact the build produces, not the
  nearest folder that contains one.** Both folders were named `Icons` and both were full of valid
  PNGs, so the wrong one worked — which is why it survived a year of releases.
- **`$(TargetDir)` is the build's answer to "what does this plugin need". Anything that packages
  from elsewhere is re-deciding that question by hand**, and will drift from it the moment the
  build learns something new — as it did the day the subset was introduced.
- **An over-inclusive bug has no symptom.** 005 was caught because users saw grey placeholders. Its
  mirror image produced a correct-looking plugin and a bigger download, and nothing was ever going
  to surface it except counting the files.
- **Assert the property, not just the presence.** The old check was `count > 0`, which the wrong
  folder satisfies comfortably. Guards on assets are worth as much as the specificity of what they
  assert.
