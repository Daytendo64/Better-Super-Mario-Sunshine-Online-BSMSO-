# Copy dist/_BSMSO.kxe into your extracted ISO Mods folder.
param(
    [string]$ModsDir = "",
    [string]$IsoFolder = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Source = Join-Path $Root "dist\_BSMSO.kxe"

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

$srcSize = (Get-Item $Source).Length
Copy-Item $Source $Dest -Force
$dstSize = (Get-Item $Dest).Length

Write-Host "Installed _BSMSO.kxe"
Write-Host "  From: $Source - $srcSize bytes"
Write-Host "  To:   $Dest - $dstSize bytes"
Write-Host ""
Write-Host "Restart Dolphin to load the latest BSMSO module."
