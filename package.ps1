param (
    [switch]$NoArchive,
    [string]$OutputDirectory = $PSScriptRoot
)

# Hinweis: Dieses Skript enthaelt bewusst keine Umlaute, damit es auch ohne BOM
# unter Windows PowerShell 5 korrekt laeuft.

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Write-TextNoBom([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, $Utf8NoBom)
}

function Remove-Bom([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        [System.IO.File]::WriteAllBytes($Path, [byte[]]($bytes[3..($bytes.Length - 1)]))
        Write-Host "BOM entfernt: $Path"
    }
}

Write-Host ""
Write-Host "=== Packe die Mod ===" -ForegroundColor Cyan
Write-Host ""

if (!(Test-Path "info.json")) { throw "info.json fehlt!" }
if (!(Test-Path "build"))     { throw "build/-Ordner fehlt!" }

Remove-Bom (Join-Path $PSScriptRoot "info.json")

$infoPath   = Join-Path $PSScriptRoot "info.json"
$infoRaw    = [System.IO.File]::ReadAllText($infoPath, $Utf8NoBom)
$modInfo    = $infoRaw | ConvertFrom-Json
$modId      = $modInfo.Id
$modVersion = $modInfo.Version

$DistDir   = Join-Path $OutputDirectory "dist"
$BackupDir = Join-Path $OutputDirectory "backup"
New-Item -Path $DistDir   -ItemType Directory -Force | Out-Null
New-Item -Path $BackupDir -ItemType Directory -Force | Out-Null

# -------------------------------------------------
# Hotfix hochzaehlen, wenn diese Version schon als ZIP existiert (x.xx.NN)
# -------------------------------------------------
while (Test-Path (Join-Path $DistDir "${modId}_v${modVersion}.zip")) {
    $parts = $modVersion.Split('.')
    if ($parts.Length -lt 3) { $parts += "00" }
    $hotfix = [int]$parts[2] + 1
    $parts[2] = $hotfix.ToString("00")
    $newVersion = ($parts -join '.')
    Write-Host "Version $modVersion existiert bereits -> $newVersion" -ForegroundColor Yellow

    $infoRaw = $infoRaw -replace ('"Version"\s*:\s*"' + [regex]::Escape($modVersion) + '"'), ('"Version": "' + $newVersion + '"')
    Write-TextNoBom $infoPath $infoRaw
    $modVersion = $newVersion
}

Write-Host "Mod-ID:  $modId"
Write-Host "Version: $modVersion"

$TmpDir    = Join-Path $DistDir "tmp"
$ZipOutDir = Join-Path $TmpDir $modId
if (Test-Path $ZipOutDir) { Remove-Item $ZipOutDir -Recurse -Force }
New-Item -Path $ZipOutDir -ItemType Directory -Force | Out-Null

foreach ($item in @("build\*", "LICENSE")) {
    Copy-Item -Path $item -Destination $ZipOutDir -Recurse -Force
}
# info.json immer ohne BOM ins Paket schreiben
Write-TextNoBom (Join-Path $ZipOutDir "info.json") $infoRaw

# eine evtl. schon im Spiel liegende info.json ebenfalls aktualisieren (ohne BOM)
$gameInfo = "C:\GAMES\Steam\steamapps\common\Derail Valley\Mods\$modId\info.json"
if (Test-Path (Split-Path $gameInfo)) { Write-TextNoBom $gameInfo $infoRaw }

if (-not $NoArchive) {
    $fileName = "${modId}_v${modVersion}.zip"
    $zipPath  = Join-Path $DistDir $fileName
    Compress-Archive -Path (Join-Path $ZipOutDir "*") -DestinationPath $zipPath -CompressionLevel Fastest -Force
    Write-Host "ZIP erstellt: dist\$fileName"

    $timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
    $sourceBackupPath = Join-Path $BackupDir "${modId}_v${modVersion}_$timestamp.zip"
    $sourceTmp = Join-Path $TmpDir "source"
    if (Test-Path $sourceTmp) { Remove-Item $sourceTmp -Recurse -Force }
    New-Item -Path $sourceTmp -ItemType Directory -Force | Out-Null

    Get-ChildItem -Path $PSScriptRoot -Recurse -File | Where-Object {
        $rel = $_.FullName.Substring($PSScriptRoot.Length).TrimStart('\','/')
        if ($rel -match '(^|[\\/])(bin|obj|build|dist|backup)([\\/]|$)') { return $false }
        return $_.Extension -in ".cs", ".csproj", ".txt", ".md", ".json", ".ps1"
    } | ForEach-Object { Copy-Item $_.FullName (Join-Path $sourceTmp $_.Name) -Force }

    Compress-Archive -Path (Join-Path $sourceTmp "*") -DestinationPath $sourceBackupPath -CompressionLevel Optimal -Force
    Remove-Item $sourceTmp -Recurse -Force
    Write-Host "Source-Backup erstellt: backup\$(Split-Path $sourceBackupPath -Leaf)"
}
else {
    Write-Host "Archivieren uebersprungen (-NoArchive)"
}

Write-Host ""
Write-Host "=== FERTIG ===" -ForegroundColor Green
Write-Host ""
