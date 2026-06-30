# Patch a BSMSO install or disc image to use the custom GameCube game ID GMSE90.
param(
    [string]$GamePath = "",
    [string]$GameId = "GMSE90"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$GameId = $GameId.Trim().ToUpperInvariant()

if ($GameId.Length -ne 6) {
    Write-Error "Game ID must be exactly 6 characters (got '$GameId')."
}

function Resolve-GamePath {
    if (-not [string]::IsNullOrWhiteSpace($GamePath)) {
        return $GamePath.Trim().Trim('"')
    }

    $launcherConfig = Join-Path $env:APPDATA "SMSO\config.json"
    if (Test-Path $launcherConfig) {
        try {
            $cfg = Get-Content $launcherConfig -Raw | ConvertFrom-Json
            if ($cfg.IsoPath -and -not [string]::IsNullOrWhiteSpace([string]$cfg.IsoPath)) {
                return [string]$cfg.IsoPath.Trim().Trim('"')
            }
        }
        catch {
            Write-Warning "Could not parse launcher config at $launcherConfig : $($_.Exception.Message)"
        }
    }

    return "C:\Users\young\OneDrive\Desktop\SME files\sys\main.dol"
}

function Resolve-DolphinUserDirectory {
    param([string]$DolphinExePath)

    if ([string]::IsNullOrWhiteSpace($DolphinExePath)) {
        return $null
    }

    $exeDirectory = Split-Path (Resolve-Path $DolphinExePath).Path -Parent
    if (Test-Path (Join-Path $exeDirectory "User")) {
        return Join-Path $exeDirectory "User"
    }

    $documentsPath = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "Dolphin Emulator"
    if (Test-Path $documentsPath) {
        return $documentsPath
    }

    return Join-Path $env:APPDATA "Dolphin Emulator"
}

function Clear-DolphinGameListCache {
    param([string]$DolphinExePath)

    $userDirectory = Resolve-DolphinUserDirectory -DolphinExePath $DolphinExePath
    if (-not $userDirectory) {
        return
    }

    $cacheDirectory = Join-Path $userDirectory "Cache"
    if (-not (Test-Path $cacheDirectory)) {
        return
    }

    $removed = 0
    Get-ChildItem $cacheDirectory -File | ForEach-Object {
        if ($_.Name -ieq "gamelist.cache" -or $_.Extension -ieq ".uidcache") {
            Remove-Item $_.FullName -Force
            $removed++
        }
    }

    if ($removed -gt 0) {
        Write-Host "Cleared Dolphin game list cache ($removed files)."
    }
}

function Resolve-DolphinExePath {
    $launcherConfig = Join-Path $env:APPDATA "SMSO\config.json"
    if (-not (Test-Path $launcherConfig)) {
        return $null
    }

    try {
        $cfg = Get-Content $launcherConfig -Raw | ConvertFrom-Json
        if ($cfg.DolphinPath -and -not [string]::IsNullOrWhiteSpace([string]$cfg.DolphinPath)) {
            return [string]$cfg.DolphinPath.Trim().Trim('"')
        }
    }
    catch {
        return $null
    }

    return $null
}

function Resolve-BootBinPath {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        Write-Error "Game path not found: $Path"
    }

    if (Test-Path $Path -PathType Leaf) {
        $leaf = Split-Path $Path -Leaf
        if ($leaf -ieq "main.dol") {
            return Join-Path (Split-Path $Path -Parent) "boot.bin"
        }

        if ($Path -match '\.(iso|gcm|gcz)$') {
            return $Path
        }

        $sysBoot = Join-Path $Path "sys\boot.bin"
        if (Test-Path $sysBoot) {
            return $sysBoot
        }

        Write-Error "Could not locate sys\boot.bin from game path: $Path"
    }

    $folderBoot = Join-Path $Path "sys\boot.bin"
    if (-not (Test-Path $folderBoot)) {
        Write-Error "Could not locate sys\boot.bin from game path: $Path"
    }
    return $folderBoot
}

function Read-GameId {
    param([string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 6) {
        Write-Error "File is too small to contain a GameCube game ID: $Path"
    }

    return [System.Text.Encoding]::ASCII.GetString($bytes, 0, 6)
}

function Set-GameId {
    param([string]$Path, [string]$TargetId)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 6) {
        Write-Error "File is too small to contain a GameCube game ID: $Path"
    }

    $encoded = [System.Text.Encoding]::ASCII.GetBytes($TargetId)
    for ($i = 0; $i -lt 6; $i++) {
        $bytes[$i] = $encoded[$i]
    }

    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

$resolvedGamePath = Resolve-GamePath
$targetPath = Resolve-BootBinPath -Path $resolvedGamePath
$currentId = Read-GameId -Path $targetPath

if ($currentId -eq $GameId) {
    Write-Host "Game ID already set to $GameId at $targetPath"
} else {
    Set-GameId -Path $targetPath -TargetId $GameId
    Write-Host "Patched game ID at $targetPath"
    Write-Host "  From: $currentId"
    Write-Host "  To:   $GameId"
}

$dolphinExe = Resolve-DolphinExePath
if ($dolphinExe) {
    Clear-DolphinGameListCache -DolphinExePath $dolphinExe
}

Write-Host ""
Write-Host "Restart Dolphin fully, then right-click the sys\main.dol entry and open Properties."
Write-Host "If it still shows GMSE01, use Config > General > Update Game List."
