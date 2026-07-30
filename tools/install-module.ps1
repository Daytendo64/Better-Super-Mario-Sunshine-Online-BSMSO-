# Install the full Better Sunshine Engine / Kuribo runtime + dist/_BSMSO.kxe
# into an extracted SMS game folder (not .kxe-only).
param(
    [string]$ModsDir = "",
    [string]$IsoFolder = "",
    [string]$GameRoot = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Source = Join-Path $Root "dist\_BSMSO.kxe"
$ReleaseZipCache = Join-Path $Root "dist\BetterSunshineEngine_RELEASE.zip"
$AppDataZipCache = Join-Path $env:APPDATA "SMSO\BetterSunshineEngine_RELEASE.zip"
$AppDataStaging = Join-Path $env:APPDATA "SMSO\bse-v4.0.0"
$BseReleaseZipUrl = "https://github.com/DotKuribo/BetterSunshineEngine/releases/download/v4.0.0/BetterSunshineEngine_RELEASE.zip"
$OfficialBseSize = 583744
$OfficialMovesetSize = 46976
$OfficialMainDolSize = 4128928

function Ensure-OfficialBsePayload {
    $stagingKuribo = Join-Path $AppDataStaging "Kuribo!"
    $stagingMain = Join-Path $AppDataStaging "main.dol"
    $stagingBoot = Join-Path $AppDataStaging "boot.bin"
    $stagingKxe = Join-Path $AppDataStaging "BetterSunshineEngine.kxe"
    $stagingKernel = Join-Path $stagingKuribo "System\KuriboKernel.bin"

    if ((Test-Path $stagingKernel) -and (Test-Path $stagingMain) -and (Test-Path $stagingBoot) -and (Test-Path $stagingKxe)) {
        Write-Host "Using cached BSE payload at $AppDataStaging"
        return
    }

    $zipPath = $null
    if (Test-Path $AppDataZipCache) {
        $zipPath = $AppDataZipCache
    } elseif (Test-Path $ReleaseZipCache) {
        $zipPath = $ReleaseZipCache
    } else {
        $tempDir = Join-Path ([IO.Path]::GetTempPath()) ("smso-bse-release-" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
        $zipPath = Join-Path $tempDir "BetterSunshineEngine_RELEASE.zip"
        try {
            Write-Host "Downloading official BetterSunshineEngine RELEASE zip..."
            Invoke-WebRequest -Uri $BseReleaseZipUrl -OutFile $zipPath
            New-Item -ItemType Directory -Force -Path (Split-Path $AppDataZipCache -Parent) | Out-Null
            Copy-Item $zipPath $AppDataZipCache -Force
            Copy-Item $zipPath $ReleaseZipCache -Force -ErrorAction SilentlyContinue
            $zipPath = $AppDataZipCache
        } finally {
            Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $extractDir = Join-Path ([IO.Path]::GetTempPath()) ("smso-bse-extract-" + [Guid]::NewGuid().ToString("N"))
    try {
        if (Test-Path $AppDataStaging) {
            Remove-Item $AppDataStaging -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $AppDataStaging | Out-Null
        New-Item -ItemType Directory -Force -Path $extractDir | Out-Null

        Write-Host "Extracting official BSE payload from $zipPath..."
        Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

        $kuribo = Get-ChildItem $extractDir -Recurse -Directory -Filter "Kuribo!" | Select-Object -First 1
        if (-not $kuribo) {
            Write-Error "BetterSunshineEngine_RELEASE.zip did not contain a Kuribo! folder."
        }

        $releaseRoot = $kuribo.FullName | Split-Path -Parent
        $mainDol = Join-Path $releaseRoot "main.dol"
        $bootBin = Join-Path $releaseRoot "boot.bin"
        $kernel = Join-Path $kuribo.FullName "System\KuriboKernel.bin"
        $bseKxe = Join-Path $kuribo.FullName "Mods\BetterSunshineEngine.kxe"
        if (-not (Test-Path $bseKxe)) {
            $bseKxe = (Get-ChildItem $extractDir -Recurse -Filter "BetterSunshineEngine.kxe" | Select-Object -First 1).FullName
        }

        if (-not ((Test-Path $mainDol) -and (Test-Path $bootBin) -and (Test-Path $kernel) -and (Test-Path $bseKxe))) {
            Write-Error "BetterSunshineEngine_RELEASE.zip is missing KuriboKernel.bin, main.dol, boot.bin, or BetterSunshineEngine.kxe."
        }

        $stagedKuribo = Join-Path $AppDataStaging "Kuribo!"
        New-Item -ItemType Directory -Force -Path $stagedKuribo | Out-Null
        robocopy $kuribo.FullName $stagedKuribo /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
        if ($LASTEXITCODE -ge 8) {
            Write-Error "Failed to stage Kuribo! folder (robocopy exit $LASTEXITCODE)"
        }
        Copy-Item $mainDol $stagingMain -Force
        Copy-Item $bootBin $stagingBoot -Force
        Copy-Item $bseKxe $stagingKxe -Force

        # Also keep legacy single-file cache for older tooling.
        $legacyKxe = Join-Path $Root "dist\BetterSunshineEngine.release.kxe"
        Copy-Item $bseKxe $legacyKxe -Force
        Copy-Item $bseKxe (Join-Path $env:APPDATA "SMSO\BetterSunshineEngine.release.kxe") -Force

        Write-Host "Staged official BSE payload at $AppDataStaging"
    } finally {
        Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Resolve-GameRoot {
    param([string]$ExplicitGameRoot, [string]$ExplicitModsDir, [string]$ExplicitIsoFolder)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitGameRoot)) {
        return $ExplicitGameRoot.Trim().Trim('"')
    }

    if (-not [string]::IsNullOrWhiteSpace($ExplicitIsoFolder)) {
        return $ExplicitIsoFolder.Trim().Trim('"')
    }

    if (-not [string]::IsNullOrWhiteSpace($ExplicitModsDir)) {
        $mods = $ExplicitModsDir.Trim().Trim('"')
        # ...\files\Kuribo!\Mods → game root is three parents up
        $kuribo = Split-Path $mods -Parent
        $files = Split-Path $kuribo -Parent
        return (Split-Path $files -Parent)
    }

    $modsPathFile = Join-Path $PSScriptRoot "mods-path.txt"
    if (Test-Path $modsPathFile) {
        $line = (Get-Content $modsPathFile -ErrorAction SilentlyContinue |
            Where-Object { $_ -and -not $_.Trim().StartsWith("#") } |
            Select-Object -First 1)
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $mods = $line.Trim().Trim('"')
            $kuribo = Split-Path $mods -Parent
            $files = Split-Path $kuribo -Parent
            return (Split-Path $files -Parent)
        }
    }

    if ($env:SMSO_MODS_DIR -and -not [string]::IsNullOrWhiteSpace($env:SMSO_MODS_DIR)) {
        $mods = $env:SMSO_MODS_DIR.Trim().Trim('"')
        $kuribo = Split-Path $mods -Parent
        $files = Split-Path $kuribo -Parent
        return (Split-Path $files -Parent)
    }

    $launcherConfig = Join-Path $env:APPDATA "SMSO\config.json"
    if (Test-Path $launcherConfig) {
        try {
            $cfg = Get-Content $launcherConfig -Raw | ConvertFrom-Json
            if ($cfg.ModsDir -and -not [string]::IsNullOrWhiteSpace([string]$cfg.ModsDir)) {
                $mods = [string]$cfg.ModsDir
                $kuribo = Split-Path $mods -Parent
                $files = Split-Path $kuribo -Parent
                return (Split-Path $files -Parent)
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
                    if ((Test-Path (Join-Path $root "sys")) -or (Test-Path (Join-Path $root "files"))) {
                        return $root
                    }
                }
            }
        }
        catch {
            Write-Warning "Could not parse launcher config at $launcherConfig : $($_.Exception.Message)"
        }
    }

    return "C:\Users\young\OneDrive\Desktop\sms online files"
}

function Install-BseRuntime {
    param([string]$TargetGameRoot)

    $stagingKuribo = Join-Path $AppDataStaging "Kuribo!"
    $stagingMain = Join-Path $AppDataStaging "main.dol"
    $stagingBoot = Join-Path $AppDataStaging "boot.bin"
    $stagingKxe = Join-Path $AppDataStaging "BetterSunshineEngine.kxe"

    $filesDir = Join-Path $TargetGameRoot "files"
    $sysDir = Join-Path $TargetGameRoot "sys"
    $kuriboDest = Join-Path $filesDir "Kuribo!"
    $systemDest = Join-Path $kuriboDest "System"
    $modsDest = Join-Path $kuriboDest "Mods"

    New-Item -ItemType Directory -Force -Path $systemDest | Out-Null
    New-Item -ItemType Directory -Force -Path $modsDest | Out-Null
    New-Item -ItemType Directory -Force -Path $sysDir | Out-Null

    Write-Host "Merging Kuribo! System → $systemDest"
    robocopy (Join-Path $stagingKuribo "System") $systemDest /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
    if ($LASTEXITCODE -ge 8) {
        Write-Error "Failed to copy Kuribo! System (robocopy exit $LASTEXITCODE)"
    }

    # Copy any other Kuribo! top-level entries except Mods
    Get-ChildItem $stagingKuribo -Force | Where-Object {
        $_.Name -notin @("System", "Mods")
    } | ForEach-Object {
        $dest = Join-Path $kuriboDest $_.Name
        if ($_.PSIsContainer) {
            robocopy $_.FullName $dest /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
        } else {
            Copy-Item $_.FullName $dest -Force
        }
    }

    $bseDest = Join-Path $modsDest "BetterSunshineEngine.kxe"
    if (Test-Path $bseDest) {
        $existingSize = (Get-Item $bseDest).Length
        if ($existingSize -ne $OfficialBseSize) {
            $backup = Join-Path $modsDest "BetterSunshineEngine.kxe.dev-backup"
            Copy-Item $bseDest $backup -Force
            Write-Warning "Replacing non-release BetterSunshineEngine.kxe ($existingSize bytes); backup: $backup"
        }
    }
    $stagingKxeSize = (Get-Item $stagingKxe).Length
    if ($stagingKxeSize -ne $OfficialBseSize) {
        Write-Error "Refusing BetterSunshineEngine.kxe ($stagingKxeSize bytes; expected $OfficialBseSize). DEBUG/dev BSE black-screens boot."
    }
    Copy-Item $stagingKxe $bseDest -Force

    # Respect launcher PatchBseMoveset (default off). Always installing Moveset left
    # Mario heavier after users turned the toggle off / never ran Install again.
    $patchMoveset = $false
    $launcherConfig = Join-Path $env:APPDATA "SMSO\config.json"
    if (Test-Path $launcherConfig) {
        try {
            $cfg = Get-Content $launcherConfig -Raw | ConvertFrom-Json
            if ($null -ne $cfg.PatchBseMoveset) {
                $patchMoveset = [bool]$cfg.PatchBseMoveset
            }
        } catch {}
    }

    $movesetDest = Join-Path $modsDest "BetterSunshineMoveset.kxe"
    if ($patchMoveset) {
        $movesetSrc = Join-Path $Root "dist\BetterSunshineMoveset.kxe"
        if (-not (Test-Path $movesetSrc)) {
            Write-Error "Missing dist\BetterSunshineMoveset.kxe"
        }
        $movesetSize = (Get-Item $movesetSrc).Length
        if ($movesetSize -ne $OfficialMovesetSize) {
            Write-Error "Refusing BetterSunshineMoveset.kxe ($movesetSize bytes; expected $OfficialMovesetSize). Wrong Moveset black-screens boot."
        }
        Copy-Item $movesetSrc $movesetDest -Force
        Write-Host "Installed BetterSunshineMoveset.kxe → $movesetDest ($((Get-Item $movesetDest).Length) bytes)"
    } elseif (Test-Path $movesetDest) {
        Remove-Item $movesetDest -Force
        Write-Host "Removed BetterSunshineMoveset.kxe (PatchBseMoveset off)"
    } else {
        Write-Host "Skipping BetterSunshineMoveset.kxe (PatchBseMoveset off)"
    }

    Copy-Item $Source (Join-Path $modsDest "_BSMSO.kxe") -Force
    Copy-Item $stagingMain (Join-Path $sysDir "main.dol") -Force
    Copy-Item $stagingBoot (Join-Path $sysDir "boot.bin") -Force

    $installedBse = (Get-Item $bseDest).Length
    $installedMain = (Get-Item (Join-Path $sysDir "main.dol")).Length
    if ($installedBse -ne $OfficialBseSize) {
        Write-Error "Post-install BetterSunshineEngine.kxe is $installedBse bytes (expected $OfficialBseSize)."
    }
    if ($installedMain -ne $OfficialMainDolSize) {
        Write-Error "Post-install main.dol is $installedMain bytes (expected $OfficialMainDolSize)."
    }
    if ($patchMoveset) {
        $installedMoveset = (Get-Item $movesetDest).Length
        if ($installedMoveset -ne $OfficialMovesetSize) {
            Write-Error "Post-install BetterSunshineMoveset.kxe is $installedMoveset bytes (expected $OfficialMovesetSize)."
        }
    } elseif (Test-Path $movesetDest) {
        Write-Error "BetterSunshineMoveset.kxe still present after PatchBseMoveset-off install."
    }
    # Patch GMSE90 into boot.bin (first 6 ASCII bytes), matching launcher GameIdentity.
    $bootDest = Join-Path $sysDir "boot.bin"
    $bytes = [System.IO.File]::ReadAllBytes($bootDest)
    $id = [System.Text.Encoding]::ASCII.GetBytes("GMSE90")
    for ($i = 0; $i -lt 6; $i++) { $bytes[$i] = $id[$i] }
    [System.IO.File]::WriteAllBytes($bootDest, $bytes)

    Write-Host "Installed KuriboKernel.bin → $(Join-Path $systemDest 'KuriboKernel.bin')"
    Write-Host "Installed BSE main.dol / boot.bin (GMSE90) → $sysDir"
    Write-Host "Installed BetterSunshineEngine.kxe → $bseDest ($((Get-Item $bseDest).Length) bytes)"
    Write-Host "Installed _BSMSO.kxe → $(Join-Path $modsDest '_BSMSO.kxe') ($((Get-Item (Join-Path $modsDest '_BSMSO.kxe')).Length) bytes)"

    # Title / options UI archives (assets/data → files/data).
    $assetsData = Join-Path $Root "assets\data"
    $destData = Join-Path $filesDir "data"
    New-Item -ItemType Directory -Force -Path $destData | Out-Null
    foreach ($name in @("nintendo.szs", "option.szs")) {
        $src = Join-Path $assetsData $name
        if (-not (Test-Path $src)) { continue }
        $dest = Join-Path $destData $name
        $backup = "$dest.bsmso-retail"
        if ((Test-Path $dest) -and -not (Test-Path $backup)) {
            Copy-Item $dest $backup -Force
            Write-Host "Backed up retail $name → $name.bsmso-retail"
        }
        Copy-Item $src $dest -Force
        Write-Host "Installed disc overlay files\data\$name ($((Get-Item $dest).Length) bytes)"
    }
}

if (-not (Test-Path $Source)) {
    Write-Error "Build first: .\tools\build.ps1"
}

$resolvedRoot = Resolve-GameRoot -ExplicitGameRoot $GameRoot -ExplicitModsDir $ModsDir -ExplicitIsoFolder $IsoFolder
if (-not (Test-Path $resolvedRoot -PathType Container)) {
    Write-Error "Game root not found: $resolvedRoot`nPass -GameRoot / -IsoFolder, or set tools\mods-path.txt / launcher config."
}

if (-not ((Test-Path (Join-Path $resolvedRoot "sys")) -or (Test-Path (Join-Path $resolvedRoot "files")))) {
    Write-Error "Not a valid extracted SMS root (need sys\ and/or files\): $resolvedRoot"
}

Ensure-OfficialBsePayload
Install-BseRuntime -TargetGameRoot $resolvedRoot

Write-Host ""
Write-Host "BSE / Kuribo runtime installed into: $resolvedRoot"
Write-Host "Restart Dolphin to load the latest modules."
