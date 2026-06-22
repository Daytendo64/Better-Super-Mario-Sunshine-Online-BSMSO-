param(
    [string]$Version = "1.0",
    [string]$LauncherDir = "",
    [string]$ServerDir = "",
    [string]$BseKxePath = "",
    [switch]$RefreshBse
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$DistDir = Join-Path $Root "dist"
$PackageName = "SMSO_but_better_$Version"
$PackageDir = Join-Path $DistDir $PackageName
$ZipPath = Join-Path $DistDir "$PackageName.zip"
$ZipRoot = "$PackageName/"
if ([string]::IsNullOrWhiteSpace($LauncherDir)) {
    $LauncherDir = Join-Path $DistDir "launcher"
}
if ([string]::IsNullOrWhiteSpace($ServerDir)) {
    $ServerDir = Join-Path $DistDir "server"
}
$ModulePath = Join-Path $DistDir "_SMSO.kxe"
$ReleaseBseCache = Join-Path $DistDir "BetterSunshineEngine.release.kxe"
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
    Write-Error "Missing dist\_SMSO.kxe. Run .\tools\build.ps1 first."
}
if (-not (Test-Path $BseKxePath)) {
    Write-Error "Missing release BetterSunshineEngine.kxe at $BseKxePath"
}
if (-not (Test-Path (Join-Path $LauncherDir "SMSO.Launcher.exe"))) {
    Write-Error "Missing published launcher. Run .\tools\publish.ps1 first."
}

if (Test-Path $PackageDir) {
    Remove-Item $PackageDir -Recurse -Force
}
if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
}

New-Item -ItemType Directory -Force -Path $PackageDir | Out-Null

$readme = @"
Super Mario Sunshine Online (SMSO) v$Version

Quick start:
1. Copy BetterSunshineEngine.kxe and _SMSO.kxe into your extracted ISO folder at files\Kuribo!\Mods\.
   BetterSunshineEngine.kxe must load before _SMSO.kxe.
2. Run SMSO.Launcher.exe.
3. In Settings, select Dolphin and your game ISO paths.
4. Launch Dolphin, enter a stage, then Host Server or Connect.

Important files:
- SMSO.Launcher.exe: main app
- BetterSunshineEngine.kxe: official BSE release module (required parent)
- _SMSO.kxe: Dolphin/BSE module
- assets\: level data used by the launcher/server
- server\: optional dedicated server host
- docs\: setup and networking guides
"@

Set-Content -Path (Join-Path $PackageDir "README_FIRST.txt") -Value $readme -Encoding ASCII
Copy-Item $BseKxePath (Join-Path $PackageDir "BetterSunshineEngine.kxe") -Force
Copy-Item $ModulePath (Join-Path $PackageDir "_SMSO.kxe") -Force
Copy-Item (Join-Path $LauncherDir "SMSO.Launcher.exe") $PackageDir -Force
Get-ChildItem $LauncherDir -File |
    Where-Object { $_.Name -ne "SMSO.Launcher.exe" } |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $PackageDir $_.Name) -Force }
Copy-Item (Join-Path $LauncherDir "assets") (Join-Path $PackageDir "assets") -Recurse -Force

if (Test-Path $ServerDir) {
    Copy-Item $ServerDir (Join-Path $PackageDir "server") -Recurse -Force
}
if (Test-Path (Join-Path $Root "docs")) {
    Copy-Item (Join-Path $Root "docs") (Join-Path $PackageDir "docs") -Recurse -Force
}
if (Test-Path (Join-Path $Root "README.md")) {
    Copy-Item (Join-Path $Root "README.md") (Join-Path $PackageDir "PROJECT_README.md") -Force
}

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
        Add-ZipFile $archive (Join-Path $PackageDir "SMSO.Launcher.exe") ($ZipRoot + "SMSO.Launcher.exe")
        Add-ZipFile $archive (Join-Path $PackageDir "BetterSunshineEngine.kxe") ($ZipRoot + "BetterSunshineEngine.kxe")
        Add-ZipFile $archive (Join-Path $PackageDir "_SMSO.kxe") ($ZipRoot + "_SMSO.kxe")
        Add-ZipFile $archive (Join-Path $PackageDir "PROJECT_README.md") ($ZipRoot + "PROJECT_README.md")
        Get-ChildItem $PackageDir -File |
            Where-Object { $_.Name -notin @("README_FIRST.txt", "SMSO.Launcher.exe", "BetterSunshineEngine.kxe", "_SMSO.kxe", "PROJECT_README.md") } |
            Sort-Object Name |
            ForEach-Object { Add-ZipFile $archive $_.FullName ($ZipRoot + $_.Name) }
        Add-ZipDirectory $archive (Join-Path $PackageDir "assets") ($ZipRoot + "assets")
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
