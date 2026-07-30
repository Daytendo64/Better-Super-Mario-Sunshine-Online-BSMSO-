param(
    [string]$Version = "2.0",
    [string]$LauncherDir = "",
    [string]$ServerDir = "",
    [string]$BseKxePath = "",
    [string]$ModulePath = "",
    [string]$Variant = "",
    [switch]$RefreshBse
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$DistDir = Join-Path $Root "dist"
$ProductName = "Better Super Mario Sunshine"
$ProductShort = "BSMSO"
$variantSuffix = if ([string]::IsNullOrWhiteSpace($Variant)) { "" } else { "_$Variant" }
$PackageName = "${ProductShort}_$Version$variantSuffix"
$PackageDir = Join-Path $DistDir $PackageName
$ZipPath = Join-Path $DistDir "$PackageName.zip"
$ZipRoot = "$PackageName/"
$LauncherExeName = "$ProductShort.Launcher.exe"
$ServerExeName = "$ProductShort.ServerHost.exe"
if ([string]::IsNullOrWhiteSpace($LauncherDir)) {
    $LauncherDir = if ($Variant -eq "lite") {
        Join-Path $DistDir "launcher-lite"
    } else {
        Join-Path $DistDir "launcher"
    }
}
if ([string]::IsNullOrWhiteSpace($ServerDir)) {
    $ServerDir = Join-Path $DistDir "server"
}
if ([string]::IsNullOrWhiteSpace($ModulePath)) {
    $ModulePath = if ($Variant -eq "lite") {
        Join-Path $DistDir "_BSMSO.lite.kxe"
    } else {
        Join-Path $DistDir "_BSMSO.kxe"
    }
}
$ReleaseBseCache = Join-Path $DistDir "BetterSunshineEngine.release.kxe"
$MovesetPath = Join-Path $DistDir "BetterSunshineMoveset.kxe"
$BseReleaseZipUrl = "https://github.com/DotKuribo/BetterSunshineEngine/releases/download/v4.0.0/BetterSunshineEngine_RELEASE.zip"

function Ensure-ReleaseBseKxe {
    param([string]$DestinationPath)

    if (-not $RefreshBse -and (Test-Path $DestinationPath)) {
        return
    }

    $tempDir = Join-Path ([IO.Path]::GetTempPath()) ("smso-bse-release-" + [Guid]::NewGuid().ToString("N"))
    $zipPath = Join-Path $tempDir "BetterSunshineEngine_RELEASE.zip"
    New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

    try {
        Write-Host "Downloading official BetterSunshineEngine RELEASE..."
        Invoke-WebRequest -Uri $BseReleaseZipUrl -OutFile $zipPath
        Expand-Archive -Path $zipPath -DestinationPath $tempDir -Force

        $releaseKxe = Get-ChildItem $tempDir -Recurse -Filter "BetterSunshineEngine.kxe" |
            Select-Object -First 1
        if (-not $releaseKxe) {
            Write-Error "BetterSunshineEngine_RELEASE.zip did not contain BetterSunshineEngine.kxe"
        }

        Copy-Item $releaseKxe.FullName $DestinationPath -Force
        $size = (Get-Item $DestinationPath).Length
        Write-Host "Cached release BSE at $DestinationPath ($size bytes)"
    } finally {
        Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ([string]::IsNullOrWhiteSpace($BseKxePath)) {
    Ensure-ReleaseBseKxe -DestinationPath $ReleaseBseCache
    $BseKxePath = $ReleaseBseCache
}

if (-not (Test-Path $ModulePath)) {
    Write-Error "Missing module at $ModulePath. Run .\tools\build.ps1 first (use -HideNameTags for lite)."
}
if (-not (Test-Path $BseKxePath)) {
    Write-Error "Missing release BetterSunshineEngine.kxe at $BseKxePath"
}
if (-not (Test-Path $MovesetPath)) {
    Write-Error "Missing dist\BetterSunshineMoveset.kxe. Place BetterSunshineMoveset.kxe in dist\."
}
if (-not (Test-Path (Join-Path $LauncherDir "BSMSO.Launcher.exe"))) {
    Write-Error "Missing published launcher. Run .\tools\publish.ps1 first."
}

# Keep assets/latest-build.json in sync so the zip-bundled launcher update check
# matches this package's ModBuildId (no GitHub fetch — local sidecar only).
$protocolCs = Join-Path $Root "launcher\SMSO.Net\ProtocolConstants.cs"
$modBuildId = 0
if (Test-Path $protocolCs) {
    $m = Select-String -Path $protocolCs -Pattern 'public const ushort ModBuildId = (\d+);' |
        Select-Object -First 1
    if ($m) { $modBuildId = [int]$m.Matches[0].Groups[1].Value }
}
$latestBuildPath = Join-Path $Root "assets\latest-build.json"
if ($modBuildId -le 0) {
    Write-Warning "Could not parse ModBuildId from ProtocolConstants.cs - skipping latest-build.json"
} else {
    $latestDir = Split-Path -Parent $latestBuildPath
    if (-not (Test-Path $latestDir)) {
        New-Item -ItemType Directory -Force -Path $latestDir | Out-Null
    }
    $latestJson = @"
{
  "modBuildId": $modBuildId,
  "versionLabel": "$Version",
  "message": "Download the latest BSMSO zip and replace BSMSO.Launcher.exe (then run Update module)."
}
"@
    Set-Content -Path $latestBuildPath -Value $latestJson -Encoding utf8
    Copy-Item $latestBuildPath (Join-Path $DistDir "latest-build.json") -Force
    Write-Host "Wrote assets/latest-build.json (ModBuildId $modBuildId)"
}

if (Test-Path $PackageDir) {
    Remove-Item $PackageDir -Recurse -Force
}
if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
}

New-Item -ItemType Directory -Force -Path $PackageDir | Out-Null

$liteNotes = ""
if ($Variant -eq "lite") {
    $liteNotes = @"

Lite client variant:
- Client Actions hides Game Modes and Connected Players (Model + Teleport remain).
- In-game player nametags are disabled in this module build.
- Same network build id as the full release - can join the same lobbies.
- Hosts that need game-mode controls should use the full BSMSO zip.

"@
}

$readme = @"
$ProductName ($ProductShort) v$Version$(if ($Variant -eq "lite") { " (Lite)" } else { "" })

Better Super Mario Sunshine (BSMSO) is online multiplayer for Super Mario Sunshine
via Dolphin Emulator and Better Sunshine Engine (BSE).
$liteNotes
Quick start:
1. Run $LauncherExeName.
2. In Settings, set your username, Dolphin path, and game ISO path (extracted folder or .iso/.gcm).
3. Settings -> Game modules -> Install / patch modules.
   Requires an original Super Mario Sunshine copy with nothing installed on it.
   This installs the full Better Sunshine Engine / Kuribo runtime:
   Kuribo!\System\ (KuriboKernel.bin), BSE-patched sys\main.dol and sys\boot.bin,
   BetterSunshineEngine.kxe, BetterSunshineMoveset.kxe, and _BSMSO.kxe. (.gcz is not supported.)
   It also overlays title/UI archives (nintendo.szs, option.szs) into files\data\.
4. Launch Dolphin, enter a stage, then Host Server or Connect.

Custom models:
- CustomModels\ ships with this zip. The launcher syncs them into your AppData
  library on launch (overwriting older bundled packs) and into the game folder
  when modules are installed / when a game path is set, so the Mario model
  dropdown matches this release.

Important files:
- $LauncherExeName - main BSMSO launcher app
- $ServerExeName - optional dedicated server host (in server\)
- BetterSunshineEngine.kxe - official BSE release module (required parent; also installed by the launcher)
- BetterSunshineMoveset.kxe - Better Sunshine Moveset module (installed with the other .kxe files)
- _BSMSO.kxe - BSMSO game module (loads after BSE)
- CustomModels\ - bundled character packs (Shadow Mario, Luigi, Needle, etc.)
- assets\ - level data + disc UI overlays (assets\data\nintendo.szs, option.szs)
- server\ - headless dedicated server host
- docs\ - setup, networking, and troubleshooting guides

Default port: 27015 (TCP + UDP).
Do not run SMSCoop alongside BSMSO.
"@

Set-Content -Path (Join-Path $PackageDir "README_FIRST.txt") -Value $readme -Encoding UTF8
Copy-Item $BseKxePath (Join-Path $PackageDir "BetterSunshineEngine.kxe") -Force
Copy-Item $MovesetPath (Join-Path $PackageDir "BetterSunshineMoveset.kxe") -Force
Copy-Item $ModulePath (Join-Path $PackageDir "_BSMSO.kxe") -Force
Copy-Item (Join-Path $LauncherDir "BSMSO.Launcher.exe") (Join-Path $PackageDir $LauncherExeName) -Force
Get-ChildItem $LauncherDir -File |
    Where-Object { $_.Name -ne "BSMSO.Launcher.exe" -and $_.Extension -ne ".pdb" } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $PackageDir $_.Name) -Force }
Copy-Item (Join-Path $LauncherDir "assets") (Join-Path $PackageDir "assets") -Recurse -Force
# Sidecar for launcher update check (prefer package root; also under assets/).
if (Test-Path $latestBuildPath) {
    Copy-Item $latestBuildPath (Join-Path $PackageDir "latest-build.json") -Force
    $pkgAssets = Join-Path $PackageDir "assets"
    if (-not (Test-Path $pkgAssets)) {
        New-Item -ItemType Directory -Force -Path $pkgAssets | Out-Null
    }
    Copy-Item $latestBuildPath (Join-Path $pkgAssets "latest-build.json") -Force
}

# Bundle custom Mario packs so recipients get the same model list without
# re-importing SZS files. Prefer AppData library (authoritative labels + arcs);
# fall back to a CustomModels folder already published next to the launcher.
$CustomModelsSrc = Join-Path $env:APPDATA "SMSO\CustomModels"
$PublishedModels = Join-Path $LauncherDir "CustomModels"
if (-not (Test-Path $CustomModelsSrc)) {
    $CustomModelsSrc = $PublishedModels
}
if (Test-Path $CustomModelsSrc) {
    $modelsDest = Join-Path $PackageDir "CustomModels"
    New-Item -ItemType Directory -Force -Path $modelsDest | Out-Null
    $libraryJson = Join-Path $CustomModelsSrc "library.json"
    if (Test-Path $libraryJson) {
        Copy-Item $libraryJson (Join-Path $modelsDest "library.json") -Force
    }
    Get-ChildItem $CustomModelsSrc -File -Filter "*.arc" | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $modelsDest $_.Name) -Force
    }
    # Optional SZS copies for re-merge; skip if missing.
    Get-ChildItem $CustomModelsSrc -File -Filter "*.szs" | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $modelsDest $_.Name) -Force
    }
    $arcCount = @(Get-ChildItem $modelsDest -File -Filter "*.arc").Count
    Write-Host "Bundled $arcCount custom model pack(s) from $CustomModelsSrc"
} else {
    Write-Warning "No CustomModels library found - zip will not include custom Mario packs."
}

if (Test-Path $ServerDir) {
    $serverPackageDir = Join-Path $PackageDir "server"
    New-Item -ItemType Directory -Force -Path $serverPackageDir | Out-Null
    Get-ChildItem $ServerDir -File |
        Where-Object { $_.Extension -ne ".pdb" } |
        ForEach-Object {
        if ($_.Name -eq "SMSO.ServerHost.exe") {
            Copy-Item $_.FullName (Join-Path $serverPackageDir $ServerExeName) -Force
        } else {
            Copy-Item $_.FullName (Join-Path $serverPackageDir $_.Name) -Force
        }
    }
    $serverAssets = Join-Path $ServerDir "assets"
    if (Test-Path $serverAssets) {
        Copy-Item $serverAssets (Join-Path $serverPackageDir "assets") -Recurse -Force
    }
}
if (Test-Path (Join-Path $Root "docs")) {
    Copy-Item (Join-Path $Root "docs") (Join-Path $PackageDir "docs") -Recurse -Force
}
if (Test-Path (Join-Path $Root "README.md")) {
    Copy-Item (Join-Path $Root "README.md") (Join-Path $PackageDir "PROJECT_README.md") -Force
}

$bsmsoReadme = @"
# $ProductName ($ProductShort)

Online multiplayer for Super Mario Sunshine - built on Better Sunshine Engine.

See README_FIRST.txt for install steps. Full docs are in docs\.
"@
Set-Content -Path (Join-Path $PackageDir "BSMSO_README.md") -Value $bsmsoReadme -Encoding UTF8

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Add-ZipFile {
    param(
        [System.IO.Compression.ZipArchive]$Archive,
        [string]$SourcePath,
        [string]$EntryName
    )

    if (Test-Path $SourcePath) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $Archive,
            $SourcePath,
            $EntryName,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}

function Add-ZipDirectory {
    param(
        [System.IO.Compression.ZipArchive]$Archive,
        [string]$SourceDir,
        [string]$EntryPrefix
    )

    if (-not (Test-Path $SourceDir)) {
        return
    }

    Get-ChildItem $SourceDir -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($SourceDir.Length).TrimStart('\', '/')
            $entry = ($EntryPrefix.TrimEnd('/', '\') + "/" + $relative).Replace('\', '/')
            Add-ZipFile -Archive $Archive -SourcePath $_.FullName -EntryName $entry
        }
}

$zipStream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
try {
    $archive = New-Object System.IO.Compression.ZipArchive(
        $zipStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false)
    try {
        Add-ZipFile $archive (Join-Path $PackageDir "README_FIRST.txt") ($ZipRoot + "README_FIRST.txt")
        Add-ZipFile $archive (Join-Path $PackageDir $LauncherExeName) ($ZipRoot + $LauncherExeName)
        Add-ZipFile $archive (Join-Path $PackageDir "BetterSunshineEngine.kxe") ($ZipRoot + "BetterSunshineEngine.kxe")
        Add-ZipFile $archive (Join-Path $PackageDir "BetterSunshineMoveset.kxe") ($ZipRoot + "BetterSunshineMoveset.kxe")
        Add-ZipFile $archive (Join-Path $PackageDir "_BSMSO.kxe") ($ZipRoot + "_BSMSO.kxe")
        Add-ZipFile $archive (Join-Path $PackageDir "BSMSO_README.md") ($ZipRoot + "BSMSO_README.md")
        Add-ZipFile $archive (Join-Path $PackageDir "PROJECT_README.md") ($ZipRoot + "PROJECT_README.md")
        Get-ChildItem $PackageDir -File |
            Where-Object { $_.Name -notin @("README_FIRST.txt", $LauncherExeName, "BetterSunshineEngine.kxe", "BetterSunshineMoveset.kxe", "_BSMSO.kxe", "PROJECT_README.md") } |
            Sort-Object Name |
            ForEach-Object { Add-ZipFile $archive $_.FullName ($ZipRoot + $_.Name) }
        Add-ZipDirectory $archive (Join-Path $PackageDir "assets") ($ZipRoot + "assets")
        Add-ZipDirectory $archive (Join-Path $PackageDir "CustomModels") ($ZipRoot + "CustomModels")
        Add-ZipDirectory $archive (Join-Path $PackageDir "server") ($ZipRoot + "server")
        Add-ZipDirectory $archive (Join-Path $PackageDir "docs") ($ZipRoot + "docs")
    } finally {
        $archive.Dispose()
    }
} finally {
    $zipStream.Dispose()
}

$zipSize = (Get-Item $ZipPath).Length
Write-Host "Packaged $ZipPath ($zipSize bytes)"
