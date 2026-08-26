<#
.SYNOPSIS
    Builds, verifies and signs the PUBLIC release artifact for TieriChallengesFFXIV.

.DESCRIPTION
    This is the ONLY sanctioned way to produce something for players.

    It builds Release and nothing else, then refuses to continue unless the artifact proves it
    is a public build. The dev build is what Trist runs in-game; shipping it would hand players
    the Challenge Creator and the developer diagnostics.

    Guards, in order:
      1. Builds Release only. Debug is never invoked.
      2. Scans the compiled DLL for developer-only string markers and ABORTS if any are present.
      3. Checks the manifest's AssemblyVersion matches the compiled assembly version, because
         API 15 validates that strictly inside a release zip and a mismatch produces
         "Failed to install plugin" for real users while passing every local test.
      4. Signs with the Sansflaire certificate and verifies the signature is Valid.
      5. Stages a zip containing exactly the files a user needs.

.NOTES
    Run from anywhere: powershell -ExecutionPolicy Bypass -File scripts\build-public.ps1
#>

[CmdletBinding()]
param(
    [switch] $SkipSigning   # escape hatch for a local smoke test; never use for a real release
)

$ErrorActionPreference = 'Stop'

$Root      = Split-Path -Parent $PSScriptRoot
$Project   = Join-Path $Root 'src\TieriChallengesFFXIV.csproj'
$Manifest  = Join-Path $Root 'TieriChallengesFFXIV.json'
$OutDir    = Join-Path $Root 'src\bin\x64\Release'
$Dll       = Join-Path $OutDir 'TieriChallengesFFXIV.dll'
$StageDir  = Join-Path $Root 'dist'
$Thumbprint = '2BD8E89BABDE8EE56906BDD577BB1E794AA797DC'

function Fail($msg) { Write-Host ''; Write-Host "  ABORT: $msg" -ForegroundColor Red; exit 1 }
function Step($msg) { Write-Host ''; Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    OK  $msg" -ForegroundColor Green }

# ── 1. Build Release, and only Release ───────────────────────────────────────
Step 'Building PUBLIC (Release) configuration'
dotnet build $Project -c Release -p:Platform=x64 --nologo -v minimal
if ($LASTEXITCODE -ne 0) { Fail 'Release build failed.' }
if (-not (Test-Path $Dll)) { Fail "Expected artifact not found: $Dll" }
Ok "built $Dll"

# ── 2. Prove it is not the dev build ─────────────────────────────────────────
Step 'Verifying no developer-only code is present'
$markers = @(
    'tc_creator',              # Challenge Creator window id
    'Draw volumes in world',   # in-world placement overlay toggle
    'Set to my current zone',  # creator zone control
    'Missing details',         # dev-only challenge flag
    'Animation: '              # dev status readout
)
$text  = [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($Dll))
$found = @($markers | Where-Object { $text.IndexOf($_) -ge 0 })

if ($found.Count -gt 0) {
    Fail ("DEV markers present in the release artifact: {0}`n" -f ($found -join ', ')) +
         '           This artifact is NOT safe to publish. Check that DEV_BUILD is only defined for Debug.'
}
Ok 'no developer markers found'

# ── 3. Manifest version must match the binary ────────────────────────────────
Step 'Checking manifest version against the compiled assembly'
$asmVersion = [Reflection.AssemblyName]::GetAssemblyName($Dll).Version.ToString()
$manifestJson = Get-Content $Manifest -Raw | ConvertFrom-Json
$manifestVersion = $manifestJson.AssemblyVersion

if ([string]::IsNullOrWhiteSpace($manifestVersion)) {
    Fail 'Manifest has no AssemblyVersion. API 15 rejects release zips without one.'
}
if ($manifestVersion -ne $asmVersion) {
    Fail "Version mismatch: assembly is $asmVersion but manifest says $manifestVersion. Update TieriChallengesFFXIV.json."
}
Ok "both report $asmVersion"

# ── 4. Sign ──────────────────────────────────────────────────────────────────
if ($SkipSigning) {
    Write-Host ''
    Write-Host '    WARNING: signing skipped. This artifact must NOT be published.' -ForegroundColor Yellow
} else {
    Step 'Signing with the Sansflaire certificate'
    $cert = Get-ChildItem "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) {
        Fail "Signing certificate $Thumbprint not found in Cert:\CurrentUser\My. See C:\Users\trist\Documents\SansflaireCertificate\README.md"
    }

    Set-AuthenticodeSignature -FilePath $Dll -Certificate $cert `
        -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' `
        -IncludeChain All | Out-Null

    $sig = Get-AuthenticodeSignature $Dll
    if ($sig.Status -ne 'Valid') { Fail "Signature status is $($sig.Status), expected Valid." }
    Ok "signed, status Valid, signer $($sig.SignerCertificate.Subject)"
}

# ── 5. Stage the release zip ─────────────────────────────────────────────────
Step 'Staging release zip'
if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
New-Item -ItemType Directory -Path $StageDir | Out-Null

$payload = Join-Path $StageDir 'payload'
New-Item -ItemType Directory -Path $payload | Out-Null

Copy-Item $Dll      $payload
Copy-Item $Manifest $payload
foreach ($lib in 'PanacheUI.dll','SkiaSharp.dll','libSkiaSharp.dll') {
    $src = Join-Path $env:APPDATA "XIVLauncher\devPlugins\PanacheUI\$lib"
    if (-not (Test-Path $src)) { Fail "Missing dependency for the zip: $src" }
    Copy-Item $src $payload
}

# PanacheUI's bundled icon PNGs.
#
# These were missing from every zip up to and including 0.81.28.0, so every icon in the plugin -
# the completion checkbox, the hint button, the close and lock controls, the difficulty meter -
# rendered as a grey placeholder for anyone who installed it normally. It was invisible here
# because PanacheIcons searches devPlugins\PanacheUI\Icons first, which only exists on this
# machine. Plugin.TrySetIconFolder points the framework at the copy packaged below.
#
# Fails the build rather than warning: a silent recurrence looks like a broken UI, not a missing
# asset, and that is precisely how it went unnoticed the first time.
#
# TAKEN FROM THE BUILD OUTPUT, NOT FROM devPlugins\PanacheUI\Icons.
#
# That distinction is the whole point of the PanacheIcon subset declared in the csproj.
# PanacheUI.Consumer.props copies exactly the 23 icons this plugin's Ico registry names into
# $(TargetDir)\Icons; the shared PanacheUI folder holds the full framework set of 167 (3.9 MB).
# Copying from the shared folder shipped 144 icons the plugin never renders in every public zip -
# roughly 3.5 MB of dead weight per download - while the csproj comment and devPlugins/CLAUDE.md
# both stated the set was subsetted. It was, in the build output nobody was reading from.
$iconsSrc = Join-Path (Split-Path $Dll -Parent) 'Icons'
if (-not (Test-Path $iconsSrc)) {
    Fail "Missing subsetted icon folder: $iconsSrc - PanacheUI.Consumer.props should have created it during the Release build."
}

$icons = @(Get-ChildItem (Join-Path $iconsSrc '*.png'))
if ($icons.Count -eq 0) { Fail "No icons in $iconsSrc - every icon in the UI would be a placeholder." }

# A sanity bound, not an exact count: the declared list is free to grow, but silently reverting to
# the whole framework set is the specific regression this is here to catch.
$sharedIcons = @(Get-ChildItem (Join-Path $env:APPDATA 'XIVLauncher\devPlugins\PanacheUI\Icons\*.png'))
if ($sharedIcons.Count -gt 0 -and $icons.Count -eq $sharedIcons.Count) {
    Fail "Packaging all $($icons.Count) framework icons - the PanacheIcon subset in the csproj is not being applied."
}

$iconsDst = Join-Path $payload 'Icons'
New-Item -ItemType Directory -Path $iconsDst | Out-Null
Copy-Item $icons $iconsDst
$iconKB = [math]::Round((($icons | Measure-Object Length -Sum).Sum) / 1KB)
Ok "bundled $($icons.Count) of $($sharedIcons.Count) icon(s), $iconKB KB"

# Cue audio. Shipped rather than read from the game's archives: the game's own copies of these
# sounds load correctly and are silenced somewhere in its mixer that no volume, category or bus
# write could reach, so the plugin plays these through Windows instead.
#
# Keep everything added here ASCII. This file is BOM-less UTF-8 and PowerShell 5.1 reads it as
# ANSI, so a non-ASCII character inside a string literal breaks the parser outright.
$soundsSrc = Join-Path $Root 'assets\sounds'
if (-not (Test-Path $soundsSrc)) { Fail "Missing cue audio folder: $soundsSrc" }

$wavs = @(Get-ChildItem (Join-Path $soundsSrc '*.wav'))
if ($wavs.Count -eq 0) { Fail "No cue audio in $soundsSrc - cues would be silent for users." }

$soundsDst = Join-Path $payload 'sounds'
New-Item -ItemType Directory -Path $soundsDst | Out-Null
Copy-Item $wavs $soundsDst
Ok "bundled $($wavs.Count) cue sound(s)"

# Built-in Appearance backgrounds. Same shape as the cue audio above.
$bgSrc = Join-Path $Root 'assets\backgrounds'
if (-not (Test-Path $bgSrc)) { Fail "Missing backgrounds folder: $bgSrc" }

# Every format BackgroundLibrary.Extensions accepts, not just PNG - the shipped set is JPEG.
$bgImages = @(Get-ChildItem $bgSrc -File | Where-Object { $_.Extension -in '.png','.jpg','.jpeg','.bmp' })
if ($bgImages.Count -eq 0) { Fail "No background images in $bgSrc - the Appearance picker would offer nothing built-in." }

# These are the single largest thing in the zip and they do not compress, so a regression to
# lossless masters would silently double the download. Loud rather than silent about the size.
$bgKB = [math]::Round((($bgImages | Measure-Object Length -Sum).Sum) / 1KB)
if ($bgKB -gt 4096) {
    Fail "Backgrounds total $bgKB KB. They ship uncompressed inside the zip, so this is most of the download - re-encode them (JPEG q94 puts the shipped four at ~1.5 MB) rather than raising this bound without a reason."
}

$bgDst = Join-Path $payload 'backgrounds'
New-Item -ItemType Directory -Path $bgDst | Out-Null

# -Path with explicit FullName. Positional binding of a Where-Object result resolves against the
# CURRENT directory rather than the file's own, so a filtered list copies as bare names and fails
# on the first file - unlike the unfiltered Get-ChildItem the icon and sound steps pass straight in.
Copy-Item -Path $bgImages.FullName -Destination $bgDst
Ok "bundled $($bgImages.Count) background image(s), $bgKB KB"

# Player-facing help document, read at runtime by HelpLibrary. Fails rather than shipping a
# build whose Help window would open on an error - see BROKEN.md 005.
$helpSrc = Join-Path $Root 'docs\HELP.md'
if (-not (Test-Path $helpSrc)) { Fail "Missing help document: $helpSrc - the Help window would have nothing to show." }
Copy-Item $helpSrc $payload
Ok "bundled the help document"

$zip = Join-Path $StageDir "TieriChallengesFFXIV-$asmVersion.zip"
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -Force

# Stable-named copy. pluginmaster.json points at
# .../releases/latest/download/TieriChallengesFFXIV.zip, which requires the release ASSET to
# carry that exact name — so upload this one, not the versioned copy.
$stable = Join-Path $StageDir 'TieriChallengesFFXIV.zip'
Copy-Item $zip $stable -Force

Remove-Item $payload -Recurse -Force
Ok "wrote $zip"
Ok "wrote $stable  (upload THIS as the release asset)"

# ── 6. Prove the bundled manifest is installable ─────────────────────────────
# Mirrors LimLoToolkit's CI check. API 15 rejects a zip whose manifest lacks
# AssemblyVersion with "Failed to install plugin", and that failure is invisible until a real
# user tries it. See devPlugins/BROKEN.md.
Step 'Verifying the manifest INSIDE the zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($stable)
try {
    $entry = $archive.Entries | Where-Object { $_.Name -eq 'TieriChallengesFFXIV.json' }
    if (-not $entry) { Fail 'TieriChallengesFFXIV.json missing from the zip.' }

    $reader = New-Object IO.StreamReader($entry.Open())
    $shipped = $reader.ReadToEnd() | ConvertFrom-Json
    $reader.Dispose()

    if (-not $shipped.AssemblyVersion)            { Fail 'Zip manifest has no AssemblyVersion.' }
    if ($shipped.AssemblyVersion -ne $asmVersion) { Fail "Zip manifest says $($shipped.AssemblyVersion), binary is $asmVersion." }
    if (-not $shipped.DalamudApiLevel)            { Fail 'Zip manifest has no DalamudApiLevel.' }
    if ($shipped.InternalName -ne 'TieriChallengesFFXIV') { Fail "Zip manifest InternalName is $($shipped.InternalName)." }

    Ok "zip manifest -> $($shipped.InternalName) v$($shipped.AssemblyVersion) API$($shipped.DalamudApiLevel)"
} finally { $archive.Dispose() }

Write-Host ''
Write-Host "PUBLIC build $asmVersion is ready." -ForegroundColor Green
Write-Host 'Next: attach the zip to a GitHub release and update pluginmaster.json.'
Write-Host ''
