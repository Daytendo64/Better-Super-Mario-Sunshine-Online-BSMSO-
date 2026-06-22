#Requires -Version 5.1
param(
    [string]$IsoPath = "",
    [switch]$Apply,
    [string]$ReferencePath = "",
    [string]$LevelsPath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ReferencePath) { $ReferencePath = Join-Path $repoRoot "assets\episode-names.ntsc-u.json" }
if (-not $LevelsPath) { $LevelsPath = Join-Path $repoRoot "assets\levels.ntsc-u.json" }

function Format-EpisodeDisplayName {
    param([int]$EpisodeId, [string]$Title, [string]$Format)
    $dash = [char]0x2014
    switch ($Format) {
        "hub" { return $Title }
        "numbered" { return "Episode $($EpisodeId + 1) $dash $Title" }
        default { return "Episode 1 $dash $Title" }
    }
}

function Try-ExtractBmgTitles {
    param([string]$Path)
    $wszstBin = Join-Path $repoRoot "tools\wszst\szs-v2.42a-r8989-cygwin64\bin"
    $wbmgt = Get-ChildItem -Path $wszstBin -Filter "wbmgt.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    $wszst = Get-ChildItem -Path $wszstBin -Filter "wszst.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $wbmgt -or -not $wszst) {
        Write-Host "wbmgt/wszst not found — using reference JSON only."
        return $false
    }
    if (-not (Test-Path $Path)) {
        Write-Host "ISO not found: $Path — using reference JSON only."
        return $false
    }

    Write-Host "BMG extraction requires wszst/wbmgt binaries (install via tools/wszst). Using reference JSON."
    return $false
}

if (-not (Test-Path $ReferencePath)) { throw "Missing reference: $ReferencePath" }
if (-not (Test-Path $LevelsPath)) { throw "Missing levels: $LevelsPath" }

if ($IsoPath) { Try-ExtractBmgTitles -Path $IsoPath | Out-Null }

$reference = Get-Content -Raw -Path $ReferencePath | ConvertFrom-Json
$levels = Get-Content -Raw -Path $LevelsPath | ConvertFrom-Json

$refByKey = @{}
foreach ($entry in $reference.entries) {
    $refByKey["$($entry.courseId):$($entry.episodeId)"] = $entry
}

$updated = 0
foreach ($course in $levels.courses) {
    foreach ($episode in $course.episodes) {
        $key = "$($course.courseId):$($episode.episodeId)"
        if (-not $refByKey.ContainsKey($key)) {
            Write-Warning "No reference for course $key"
            continue
        }
        $ref = $refByKey[$key]
        $newName = Format-EpisodeDisplayName -EpisodeId $episode.episodeId -Title $ref.title -Format $ref.format
        if ($episode.displayName -ne $newName) {
            $episode.displayName = $newName
            $updated++
        }
    }
}

Write-Host "Reference source: $($reference.source); names changed: $updated"
Write-Host "Tip: EpisodeNameReference.ApplyToLevels() in SMSO.Net preserves course displayName encoding."

if ($Apply) {
    $json = $levels | ConvertTo-Json -Depth 8
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [IO.File]::WriteAllText($LevelsPath, $json, $utf8NoBom)
    Write-Host "Wrote $LevelsPath"
}
else {
    Write-Host "Dry run - use -Apply to write levels.ntsc-u.json"
}
