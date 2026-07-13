# Publish launcher and server host
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$LauncherDir = Join-Path $Root "launcher"
$DistLauncher = Join-Path $Root "dist\launcher"
$DistServer = Join-Path $Root "dist\server"
$AssetsSrc = Join-Path $Root "assets"

if (Test-Path $DistLauncher) {
    Remove-Item $DistLauncher -Recurse -Force
}
if (Test-Path $DistServer) {
    Remove-Item $DistServer -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $DistLauncher | Out-Null
New-Item -ItemType Directory -Force -Path $DistServer | Out-Null

Push-Location $LauncherDir
dotnet publish SMSO.Launcher\SMSO.Launcher.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true -o $DistLauncher
dotnet publish SMSO.ServerHost\SMSO.ServerHost.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false `
    -p:IncludeNativeLibrariesForSelfExtract=true -o $DistServer
Pop-Location

# Bundle level data
$assetsDest = Join-Path $DistLauncher "assets"
New-Item -ItemType Directory -Force -Path $assetsDest | Out-Null
Copy-Item (Join-Path $AssetsSrc "levels.ntsc-u.json") $assetsDest -Force
Copy-Item (Join-Path $AssetsSrc "episode-names.ntsc-u.json") $assetsDest -Force
$serverAssetsDest = Join-Path $DistServer "assets"
New-Item -ItemType Directory -Force -Path $serverAssetsDest | Out-Null
Copy-Item (Join-Path $AssetsSrc "levels.ntsc-u.json") $serverAssetsDest -Force
Copy-Item (Join-Path $AssetsSrc "episode-names.ntsc-u.json") $serverAssetsDest -Force

# Bundle custom Mario packs next to the published launcher (same layout as the zip).
$CustomModelsSrc = Join-Path $env:APPDATA "SMSO\CustomModels"
$modelsDest = Join-Path $DistLauncher "CustomModels"
if (Test-Path $CustomModelsSrc) {
    if (Test-Path $modelsDest) {
        Remove-Item $modelsDest -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $modelsDest | Out-Null
    $libraryJson = Join-Path $CustomModelsSrc "library.json"
    if (Test-Path $libraryJson) {
        Copy-Item $libraryJson (Join-Path $modelsDest "library.json") -Force
    }
    Get-ChildItem $CustomModelsSrc -File -Filter "*.arc" | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $modelsDest $_.Name) -Force
    }
    Get-ChildItem $CustomModelsSrc -File -Filter "*.szs" | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $modelsDest $_.Name) -Force
    }
    $arcCount = @(Get-ChildItem $modelsDest -File -Filter "*.arc").Count
    Write-Host "Published $arcCount CustomModels pack(s) from $CustomModelsSrc"
} else {
    Write-Warning "No AppData CustomModels library - published launcher will not include packs."
}

# Optional Authenticode signing (set CODESIGN_PFX + CODESIGN_PASSWORD env vars)
$pfxPath = $env:CODESIGN_PFX
$pfxPassword = $env:CODESIGN_PASSWORD
if ($pfxPath -and (Test-Path $pfxPath)) {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($signtool) {
        $timestamp = "http://timestamp.digicert.com"
        $exes = @(
            (Join-Path $DistLauncher "BSMSO.Launcher.exe"),
            (Join-Path $DistServer "SMSO.ServerHost.exe")
        )
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
foreach ($dir in @($DistLauncher, $DistServer)) {
    Get-ChildItem $dir -Filter *.exe | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        $relative = $_.FullName.Substring($Root.Length + 1)
        $hashLines += "$hash  $relative"
    }
}
if ($hashLines.Count -gt 0) {
    $hashLines | Set-Content $checksumsPath -Encoding UTF8
    Write-Host "Wrote dist/CHECKSUMS.txt"
}

Write-Host "Published to dist/launcher and dist/server"
