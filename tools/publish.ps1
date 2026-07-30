# Publish launcher and server host
param(
    [switch]$ClientLite,
    [string]$LauncherOutDir = "",
    [string]$ServerOutDir = "",
    [switch]$SkipServer,
    [switch]$SkipLauncher
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$LauncherDir = Join-Path $Root "launcher"
$DistLauncher = if ([string]::IsNullOrWhiteSpace($LauncherOutDir)) {
    if ($ClientLite) { Join-Path $Root "dist\launcher-lite" } else { Join-Path $Root "dist\launcher" }
} else {
    $LauncherOutDir
}
$DistServer = if ([string]::IsNullOrWhiteSpace($ServerOutDir)) {
    Join-Path $Root "dist\server"
} else {
    $ServerOutDir
}
$AssetsSrc = Join-Path $Root "assets"

# Preserve CustomModels across dist wipe so publish does not drop packs when
# %AppData%\SMSO\CustomModels is empty (CI / fresh machine).
$stashedCustomModels = $null
if (-not $SkipLauncher -and (Test-Path (Join-Path $DistLauncher "CustomModels"))) {
    $stashedCustomModels = Join-Path $env:TEMP ("bsmso-cm-stash-" + [guid]::NewGuid().ToString("N"))
    Copy-Item (Join-Path $DistLauncher "CustomModels") $stashedCustomModels -Recurse -Force
    Write-Host "Stashed existing CustomModels before dist wipe -> $stashedCustomModels"
}

if (-not $SkipLauncher -and (Test-Path $DistLauncher)) {
    Remove-Item $DistLauncher -Recurse -Force
}
if (-not $SkipServer -and (Test-Path $DistServer)) {
    Remove-Item $DistServer -Recurse -Force
}

if (-not $SkipLauncher) {
    New-Item -ItemType Directory -Force -Path $DistLauncher | Out-Null
}
if (-not $SkipServer) {
    New-Item -ItemType Directory -Force -Path $DistServer | Out-Null
}

$liteProp = if ($ClientLite) { "-p:BSMSOClientLite=true" } else { "-p:BSMSOClientLite=false" }

Push-Location $LauncherDir
if (-not $SkipLauncher) {
    # DefineConstants (BSMSO_CLIENT_LITE) must recompile SMSO.Net; incremental
    # builds can keep a stale BuildFeatures.ClientLite value otherwise.
    dotnet clean SMSO.Net\SMSO.Net.csproj -c Release --nologo -v q
    dotnet clean SMSO.Launcher\SMSO.Launcher.csproj -c Release --nologo -v q
    dotnet publish SMSO.Launcher\SMSO.Launcher.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        $liteProp `
        -o $DistLauncher
}
if (-not $SkipServer) {
    dotnet publish SMSO.ServerHost\SMSO.ServerHost.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false `
        -p:IncludeNativeLibrariesForSelfExtract=true -o $DistServer
}
Pop-Location

# Bundle level data + disc UI overlays (title / options menus)
if (-not $SkipLauncher) {
    $assetsDest = Join-Path $DistLauncher "assets"
    New-Item -ItemType Directory -Force -Path $assetsDest | Out-Null
    Copy-Item (Join-Path $AssetsSrc "levels.ntsc-u.json") $assetsDest -Force
    Copy-Item (Join-Path $AssetsSrc "episode-names.ntsc-u.json") $assetsDest -Force
    $latestBuildSrc = Join-Path $AssetsSrc "latest-build.json"
    if (Test-Path $latestBuildSrc) {
        Copy-Item $latestBuildSrc $assetsDest -Force
        Copy-Item $latestBuildSrc $DistLauncher -Force
    }
    $dataSrc = Join-Path $AssetsSrc "data"
    if (Test-Path $dataSrc) {
        $dataDest = Join-Path $assetsDest "data"
        New-Item -ItemType Directory -Force -Path $dataDest | Out-Null
        foreach ($name in @("nintendo.szs", "option.szs")) {
            $src = Join-Path $dataSrc $name
            if (Test-Path $src) {
                Copy-Item $src (Join-Path $dataDest $name) -Force
            }
        }
        Write-Host "Published disc data overlays from $dataSrc"
    }
}
if (-not $SkipServer) {
    $serverAssetsDest = Join-Path $DistServer "assets"
    New-Item -ItemType Directory -Force -Path $serverAssetsDest | Out-Null
    Copy-Item (Join-Path $AssetsSrc "levels.ntsc-u.json") $serverAssetsDest -Force
    Copy-Item (Join-Path $AssetsSrc "episode-names.ntsc-u.json") $serverAssetsDest -Force
}

# Bundle Kuribo .kxe modules next to the launcher (Update module / Install resolve
# TryFindSourceModule from the exe directory first). Without these, Update can stamp
# a new ModBuildId while leaving a stale _BSMSO.kxe from a parent folder.
if (-not $SkipLauncher) {
    $DistRoot = Join-Path $Root "dist"
    foreach ($name in @("_BSMSO.kxe", "BetterSunshineMoveset.kxe", "BetterSunshineEngine.kxe")) {
        $src = Join-Path $DistRoot $name
        if ($ClientLite -and $name -eq "_BSMSO.kxe") {
            $lite = Join-Path $DistRoot "_BSMSO.lite.kxe"
            if (Test-Path $lite) { $src = $lite }
        }
        if (Test-Path $src) {
            $destName = if ($ClientLite -and $name -eq "_BSMSO.kxe") { "_BSMSO.kxe" } else { $name }
            Copy-Item $src (Join-Path $DistLauncher $destName) -Force
            Write-Host "Published $destName beside launcher ($((Get-Item $src).Length) bytes)"
        } else {
            Write-Warning "Missing dist\$name - launcher Update module will not find a bundled $name beside the exe."
        }
    }
}

# Bundle custom Mario packs next to the published launcher (same layout as the zip).
# Prefer AppData; if empty, restore CustomModels stashed before the dist wipe so a
# publish on a clean machine does not silently drop packs from the previous build.
if (-not $SkipLauncher) {
    $CustomModelsSrc = $null
    $appDataModels = Join-Path $env:APPDATA "SMSO\CustomModels"
    if ((Test-Path $appDataModels) -and @(Get-ChildItem $appDataModels -File -Filter "*.arc" -ErrorAction SilentlyContinue).Count -gt 0) {
        $CustomModelsSrc = $appDataModels
    } elseif ($stashedCustomModels -and (Test-Path $stashedCustomModels)) {
        $CustomModelsSrc = $stashedCustomModels
        Write-Host "AppData CustomModels empty/missing - restoring stashed packs from previous dist/launcher."
    }

    $modelsDest = Join-Path $DistLauncher "CustomModels"
    if ($CustomModelsSrc -and (Test-Path $CustomModelsSrc)) {
        if (Test-Path $modelsDest) {
            Remove-Item $modelsDest -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $modelsDest | Out-Null
        $libraryJson = Join-Path $CustomModelsSrc "library.json"
        if (Test-Path $libraryJson) {
            Copy-Item $libraryJson (Join-Path $modelsDest "library.json") -Force
        } else {
            Write-Warning "CustomModels source has .arc packs but no library.json - display-named packs will not seed on first Install."
        }
        Get-ChildItem $CustomModelsSrc -File -Filter "*.arc" | ForEach-Object {
            Copy-Item $_.FullName (Join-Path $modelsDest $_.Name) -Force
        }
        Get-ChildItem $CustomModelsSrc -File -Filter "*.szs" | ForEach-Object {
            Copy-Item $_.FullName (Join-Path $modelsDest $_.Name) -Force
        }
        $arcCount = @(Get-ChildItem $modelsDest -File -Filter "*.arc").Count
        $hasLibrary = Test-Path (Join-Path $modelsDest "library.json")
        Write-Host "Published $arcCount CustomModels pack(s) from $CustomModelsSrc (library.json=$hasLibrary)"
        if ($arcCount -eq 0) {
            Write-Warning "Published CustomModels folder has zero .arc files."
        } elseif (-not $hasLibrary) {
            Write-Warning "Published CustomModels is missing library.json - first-time Install will skip display-named packs."
        }
    } else {
        Write-Warning "No AppData or stashed CustomModels library - published launcher will not include packs."
    }

    if ($stashedCustomModels -and (Test-Path $stashedCustomModels)) {
        Remove-Item $stashedCustomModels -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Optional Authenticode signing (set CODESIGN_PFX + CODESIGN_PASSWORD env vars)
$pfxPath = $env:CODESIGN_PFX
$pfxPassword = $env:CODESIGN_PASSWORD
if ($pfxPath -and (Test-Path $pfxPath)) {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($signtool) {
        $timestamp = "http://timestamp.digicert.com"
        $exes = @()
        if (-not $SkipLauncher) {
            $exes += (Join-Path $DistLauncher "BSMSO.Launcher.exe")
        }
        if (-not $SkipServer) {
            $exes += (Join-Path $DistServer "SMSO.ServerHost.exe")
        }
        foreach ($exe in $exes) {
            if (Test-Path $exe) {
                if ($pfxPassword) {
                    & signtool sign /fd SHA256 /tr $timestamp /td SHA256 /f $pfxPath /p $pfxPassword $exe
                } else {
                    & signtool sign /fd SHA256 /tr $timestamp /td SHA256 /f $pfxPath $exe
                }
            }
        }
        Write-Host "Signed published executables."
    } else {
        Write-Warning "signtool.exe not found - skipping code signing."
    }
}

# Release checksums for SmartScreen / AV verification
$checksumsPath = Join-Path $Root "dist\CHECKSUMS.txt"
$hashLines = @()
$dirs = @()
if (-not $SkipLauncher) { $dirs += $DistLauncher }
if (-not $SkipServer) { $dirs += $DistServer }
foreach ($dir in $dirs) {
    Get-ChildItem $dir -Filter *.exe -ErrorAction SilentlyContinue | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        $relative = $_.FullName.Substring($Root.Length + 1)
        $hashLines += "$hash  $relative"
    }
}
if ($hashLines.Count -gt 0) {
    $hashLines | Set-Content $checksumsPath -Encoding UTF8
    Write-Host "Wrote dist/CHECKSUMS.txt"
}

$launcherLabel = if ($SkipLauncher) { "skipped" } else { $DistLauncher }
$serverLabel = if ($SkipServer) { "skipped" } else { $DistServer }
Write-Host "Published ClientLite=$ClientLite"
Write-Host "  launcher -> $launcherLabel"
Write-Host "  server   -> $serverLabel"
