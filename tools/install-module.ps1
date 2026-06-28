# Copy dist/_BSMSO.kxe into your extracted ISO Mods folder.
param(
    [string]$ModsDir = "",
    [string]$IsoFolder = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Source = Join-Path $Root "dist\_BSMSO.kxe"
$ReleaseBseCache = Join-Path $Root "dist\BetterSunshineEngine.release.kxe"
$BseReleaseZipUrl = "https://github.com/DotKuribo/BetterSunshineEngine/releases/download/v4.0.0/BetterSunshineEngine_RELEASE.zip"
$OfficialBseSize = 583744

function Ensure-ReleaseBseKxe {
    param([string]$DestinationPath)

    if (Test-Path $DestinationPath) {
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

function Resolve-ModsDirectory {
    param([string]$ExplicitModsDir, [string]$ExplicitIsoFolder)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitModsDir)) {
        return $ExplicitModsDir.Trim().Trim('"')
    }

    if (-not [string]::IsNullOrWhiteSpace($ExplicitIsoFolder)) {
        return Join-Path $ExplicitIsoFolder.Trim().Trim('"') "files\Kuribo!\Mods"
    }

    $modsPathFile = Join-Path $PSScriptRoot "mods-path.txt"
    if (Test-Path $modsPathFile) {
        $line = (Get-Content $modsPathFile -ErrorAction SilentlyContinue |
            Where-Object { $_ -and -not $_.Trim().StartsWith("#") } |
            Select-Object -First 1)
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            return $line.Trim().Trim('"')
        }
    }

    if ($env:SMSO_MODS_DIR -and -not [string]::IsNullOrWhiteSpace($env:SMSO_MODS_DIR)) {
        return $env:SMSO_MODS_DIR.Trim().Trim('"')
    }

    $launcherConfig = Join-Path $env:APPDATA "SMSO\config.json"
    if (Test-Path $launcherConfig) {
        try {
            $cfg = Get-Content $launcherConfig -Raw | ConvertFrom-Json
            if ($cfg.ModsDir -and -not [string]::IsNullOrWhiteSpace([string]$cfg.ModsDir)) {
                return [string]$cfg.ModsDir
            }

            $isoPath = [string]$cfg.IsoPath
            if (-not [string]::IsNullOrWhiteSpace($isoPath)) {
                $isoPath = $isoPath.Trim().Trim('"')
                $candidates = @()

                if ($isoPath -match '\\sys\\main\.dol$') {
                    $candidates += (Split-Path (Split-Path $isoPath -Parent) -Parent)
                }

                if (Test-Path $isoPath -PathType Leaf) {
                    $candidates += (Split-Path $isoPath -Parent)
                    $candidates += (Split-Path (Split-Path $isoPath -Parent) -Parent)
                } elseif (Test-Path $isoPath -PathType Container) {
                    $candidates += $isoPath
                }

                foreach ($root in $candidates | Select-Object -Unique) {
                    if ([string]::IsNullOrWhiteSpace($root)) { continue }
                    $mods = Join-Path $root "files\Kuribo!\Mods"
                    if (Test-Path $mods) {
                        return $mods
                    }
                }
            }
        }
        catch {
            Write-Warning "Could not parse launcher config at $launcherConfig : $($_.Exception.Message)"
        }
    }

    return "C:\Users\young\OneDrive\Desktop\sms online files\files\Kuribo!\Mods"
}

$ModsDir = Resolve-ModsDirectory -ExplicitModsDir $ModsDir -ExplicitIsoFolder $IsoFolder
$Dest = Join-Path $ModsDir "_BSMSO.kxe"

if (-not (Test-Path $Source)) {
    Write-Error "Build first: .\tools\build.ps1"
}

if (-not (Test-Path $ModsDir)) {
    Write-Error "Mods folder not found: $ModsDir`nSet tools\mods-path.txt, SMSO_MODS_DIR, or launcher config ModsDir."
}

Ensure-ReleaseBseKxe -DestinationPath $ReleaseBseCache

$bseDest = Join-Path $ModsDir "BetterSunshineEngine.kxe"
$installedDevBse = $false
if (Test-Path $bseDest) {
    $installedDevBse = (Get-Item $bseDest).Length -ne $OfficialBseSize
}

if ($installedDevBse) {
    $backup = Join-Path $ModsDir "BetterSunshineEngine.kxe.dev-backup"
    Copy-Item $bseDest $backup -Force
    Write-Warning "Replacing non-release BetterSunshineEngine.kxe ($((Get-Item $bseDest).Length) bytes) with official v4.0.0 release."
    Write-Warning "Dev build backed up to $backup"
}

Copy-Item $ReleaseBseCache $bseDest -Force
$bseSize = (Get-Item $bseDest).Length

$srcSize = (Get-Item $Source).Length
Copy-Item $Source $Dest -Force
$dstSize = (Get-Item $Dest).Length

Write-Host "Installed BetterSunshineEngine.kxe"
Write-Host "  From: $ReleaseBseCache - $bseSize bytes"
Write-Host "  To:   $bseDest - $bseSize bytes"
Write-Host ""
Write-Host "Installed _BSMSO.kxe"
Write-Host "  From: $Source - $srcSize bytes"
Write-Host "  To:   $Dest - $dstSize bytes"
Write-Host ""
Write-Host "Restart Dolphin to load the latest BSMSO module."
