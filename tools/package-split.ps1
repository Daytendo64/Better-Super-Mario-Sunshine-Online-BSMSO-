# Package the same published build under multiple product version labels.
# Same ModBuildId / binaries; only zip name, README, and latest-build.json versionLabel differ.
#
# Usage:
#   .\tools\package-split.ps1
#   .\tools\package-split.ps1 -Versions 1.1,2.0
#   .\tools\package-split.ps1 -Versions 1.1,2.0 -IncludeLite
#
# Prerequisites: .\tools\build.ps1 and .\tools\publish.ps1 (and lite publish if -IncludeLite).

param(
    [string[]]$Versions = @("2.0"),
    [switch]$IncludeLite,
    [switch]$RefreshBse
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$PackageRelease = Join-Path $PSScriptRoot "package-release.ps1"

if (-not $Versions -or $Versions.Count -eq 0) {
    Write-Error "Provide at least one -Versions entry (e.g. -Versions 1.1,2.0)."
}

$flat = @()
foreach ($entry in $Versions) {
    foreach ($part in ($entry -split ",")) {
        $v = $part.Trim()
        if (-not [string]::IsNullOrWhiteSpace($v)) {
            $flat += $v
        }
    }
}
$Versions = $flat | Select-Object -Unique

Write-Host "Packaging shared build as version label(s): $($Versions -join ', ')"

$produced = @()
foreach ($version in $Versions) {
    Write-Host ""
    Write-Host "=== Full package $version ===" -ForegroundColor Cyan
    & $PackageRelease -Version $version -RefreshBse:$RefreshBse
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
        Write-Error "package-release failed for version $version (full)"
    }
    $produced += (Join-Path $Root "dist\BSMSO_$version.zip")

    if ($IncludeLite) {
        Write-Host ""
        Write-Host "=== Lite package $version ===" -ForegroundColor Cyan
        & $PackageRelease -Version $version -Variant lite -RefreshBse:$RefreshBse
        if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) {
            Write-Error "package-release failed for version $version (lite)"
        }
        $produced += (Join-Path $Root "dist\BSMSO_${version}_lite.zip")
    }
}

Write-Host ""
Write-Host "Done. Produced:" -ForegroundColor Green
foreach ($path in $produced) {
    if (Test-Path $path) {
        $size = (Get-Item $path).Length
        Write-Host ("  {0} ({1:N0} bytes)" -f $path, $size)
    } else {
        Write-Warning "Missing expected zip: $path"
    }
}
