# Build + publish + package the BSMSO Lite client zip.
# Does NOT overwrite dist\BSMSO_1.0.zip or dist\_BSMSO.kxe (full release).
#
# Lite differences:
# - Client Actions: Game Modes + Connected Players hidden
# - Module: in-game nametags disabled (SMSO_HIDE_NAMETAGS)
#
# Output: dist\BSMSO_<Version>_lite.zip

param(
    [string]$Version = "1.0"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot

Write-Host "=== BSMSO Lite: module (hide nametags) ==="
& (Join-Path $PSScriptRoot "build.ps1") -HideNameTags
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$serverExe = Join-Path $Root "dist\server\SMSO.ServerHost.exe"
if (-not (Test-Path $serverExe)) {
    Write-Host "=== BSMSO Lite: publishing server host ==="
    & (Join-Path $PSScriptRoot "publish.ps1") -SkipLauncher
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "=== BSMSO Lite: launcher (ClientLite UI) ==="
& (Join-Path $PSScriptRoot "publish.ps1") -ClientLite -SkipServer
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "=== BSMSO Lite: package zip ==="
& (Join-Path $PSScriptRoot "package-release.ps1") -Version $Version -Variant lite
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$zip = Join-Path $Root "dist\BSMSO_${Version}_lite.zip"
Write-Host "Lite zip ready: $zip"
