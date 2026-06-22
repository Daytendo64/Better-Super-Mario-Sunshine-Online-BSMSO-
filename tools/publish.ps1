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
    -p:PublishSingleFile=true -o $DistLauncher
dotnet publish SMSO.ServerHost\SMSO.ServerHost.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o $DistServer
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

Write-Host "Published to dist/launcher and dist/server"
