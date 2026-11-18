# Controlla che gli exe scaricati siano quelli originali della release.
# Uso: metti VERIFY.ps1, SHA256SUMS.txt e gli exe nella stessa cartella, poi in PowerShell:
#   powershell -ExecutionPolicy Bypass -File .\VERIFY.ps1
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$sumsFile = Join-Path $here "SHA256SUMS.txt"
if (-not (Test-Path $sumsFile)) { Write-Host "Manca SHA256SUMS.txt nella cartella." -ForegroundColor Red; exit 1 }

$ok = $true
$checked = 0
foreach ($line in Get-Content $sumsFile) {
    if ($line -notmatch '^([0-9a-fA-F]{64})\s+(.+)$') { continue }
    $expected = $Matches[1].ToLowerInvariant()
    $name = $Matches[2].Trim()
    $path = Join-Path $here $name
    if (-not (Test-Path $path)) { Write-Host "-  $name non scaricato (salto)"; continue }
    $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $checked++
    if ($actual -eq $expected) { Write-Host "OK $name" -ForegroundColor Green }
    else { Write-Host "NO ${name}: il codice non corrisponde. Non aprirlo." -ForegroundColor Red; $ok = $false }
}

if ($checked -eq 0) { Write-Host "Nessun file da controllare." -ForegroundColor Yellow; exit 1 }
if ($ok) { Write-Host "Tutto a posto: i file sono quelli originali." -ForegroundColor Green; exit 0 }
exit 1
