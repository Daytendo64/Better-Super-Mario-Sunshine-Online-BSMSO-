# Build BSMSO BSE module -> dist/_BSMSO.kxe (or a lite variant)

param(
    [switch]$ParticleOnly,
    [switch]$HideNameTags,
    [switch]$SkipInstall,
    [string]$OutFileName = ""
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$ModuleDir = Join-Path $Root "module"
$DistDir = Join-Path $Root "dist"
$BseDir = Join-Path $ModuleDir "lib\BetterSunshineEngine"
$Toolchain = Join-Path $ModuleDir "targets\GCNKuriboClangRelease.cmake"

if ([string]::IsNullOrWhiteSpace($OutFileName)) {
    $OutFileName = if ($HideNameTags) { "_BSMSO.lite.kxe" } else { "_BSMSO.kxe" }
}

# Keep lite and full module cmake caches separate so option flips do not stick.
$BuildDir = if ($HideNameTags) {
    Join-Path $ModuleDir "build-lite"
} else {
    Join-Path $ModuleDir "build"
}

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

if (-not (Test-Path (Join-Path $BseDir "CMakeLists.txt"))) {
    Write-Host "Initializing BSE submodule..."
    Push-Location $Root
    git submodule update --init --recursive
    Pop-Location
}

Write-Host "Ensuring BSE submodules and LFS assets..."
Push-Location $BseDir
git submodule update --init --recursive
git lfs pull
Pop-Location

if (-not (Test-Path (Join-Path $BseDir "compiler\clang.exe"))) {
    Write-Error "BSE PowerPC compiler not found at $BseDir\compiler\clang.exe. Run: cd module\lib\BetterSunshineEngine; git lfs pull"
}

Push-Location $ModuleDir

$cmakeArgs = @(
    "-B", $BuildDir,
    "-G", "Ninja",
    "-DCMAKE_TOOLCHAIN_FILE=$Toolchain",
    "-DSMS_REGION=us"
)

if ($ParticleOnly) {
    $cmakeArgs += "-DSMSO_REMOTE_ENEMY_MARIO=OFF"
} else {
    $cmakeArgs += "-DSMSO_REMOTE_ENEMY_MARIO=ON"
}

if ($HideNameTags) {
    $cmakeArgs += "-DSMSO_HIDE_NAMETAGS=ON"
} else {
    $cmakeArgs += "-DSMSO_HIDE_NAMETAGS=OFF"
}

Write-Host "Configuring SMSO module with Kuribo toolchain..."
cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { Pop-Location; exit 1 }

Write-Host "Building _SMSO.kxe..."
cmake --build $BuildDir --target _SMSO.kxe
if ($LASTEXITCODE -ne 0) { Pop-Location; exit 1 }

$kxe = Join-Path $BuildDir "_SMSO.kxe"
if (Test-Path $kxe) {
    $dest = Join-Path $DistDir $OutFileName
    Copy-Item $kxe $dest -Force
    $size = (Get-Item $dest).Length
    Write-Host "Built dist\$OutFileName ($size bytes)"

    $shouldInstall = -not $SkipInstall -and -not $HideNameTags
    $InstallScript = Join-Path $PSScriptRoot "install-module.ps1"
    if ($shouldInstall -and (Test-Path $InstallScript)) {
        Write-Host "Deploying module to Kuribo Mods..."
        & $InstallScript
    } elseif ($HideNameTags) {
        Write-Host "Skip deploy: lite/hide-nametag build (does not overwrite installed _BSMSO.kxe)"
    } else {
        Write-Host "Skip deploy: install-module.ps1 not found or -SkipInstall"
    }
} else {
    Write-Error "Build succeeded but _SMSO.kxe was not produced in $BuildDir"
}

Pop-Location
