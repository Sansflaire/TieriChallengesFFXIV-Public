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

$bgImages = @(Get-ChildItem (Join-Path $bgSrc '*.png'))
if ($bgImages.Count -eq 0) { Fail "No background images in $bgSrc - the Appearance picker would offer nothing built-in." }

$bgDst = Join-Path $payload 'backgrounds'
New-Item -ItemType Directory -Path $bgDst | Out-Null
Copy-Item $bgImages $bgDst
Ok "bundled $($bgImages.Count) background image(s)"

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
