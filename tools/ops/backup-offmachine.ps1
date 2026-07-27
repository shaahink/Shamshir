# backup-offmachine.ps1 — E0(a), iter-pass-economics.
# Off-machine backup of the two irreplaceable artifacts:
#   1. src/TradingEngine.Web/data/trading.db  (~11.5 GB — every experiment, trade ledger, venue spec capture)
#   2. C:\ShamshirData\backfill               (~1.5 GB — Dukascopy archive, 99.976% durable, re-downloadable only slowly)
# The machine has ONE physical drive; a local copy is not a backup. Point -Destination at an
# external drive or a synced cloud folder. Needs ~14 GB free at the destination.
#
# Usage:
#   powershell -File tools\ops\backup-offmachine.ps1 -Destination E:\
#   powershell -File tools\ops\backup-offmachine.ps1 -Destination E:\ -Hash   # adds SHA256 manifest (slow)
#
# Safety: uses sqlite3 online .backup (WAL-correct, works while the app runs; F84: no VACUUM).
# robocopy /E never deletes at the destination. Verifies with PRAGMA quick_check + row-count
# comparison against the source before declaring success.

param(
    [Parameter(Mandatory = $true)] [string]$Destination,
    [switch]$Hash,
    [switch]$SkipArchive
)

$ErrorActionPreference = 'Stop'
$SrcDb = 'C:\code\Shamshir\src\TradingEngine.Web\data\trading.db'
$SrcArchive = 'C:\ShamshirData\backfill'

if (-not (Test-Path $SrcDb)) { throw "source DB not found: $SrcDb" }
if (-not (Test-Path $Destination)) { throw "destination not found: $Destination (attach the drive first)" }
$sqlite = (Get-Command sqlite3 -ErrorAction Stop).Source

$free = (Get-PSDrive -Name ((Get-Item $Destination).PSDrive.Name)).Free
$needed = (Get-Item $SrcDb).Length + 2GB
if ($free -lt $needed) { throw ("destination has {0:N1} GB free, need ~{1:N1} GB" -f ($free/1GB), ($needed/1GB)) }

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$destRoot = Join-Path $Destination "shamshir-backup-$stamp"
New-Item -ItemType Directory -Force $destRoot | Out-Null
$destDb = Join-Path $destRoot 'trading.db'

Write-Host "[1/4] sqlite online backup -> $destDb (this copies ~11.5 GB; minutes, not seconds)"
& $sqlite $SrcDb ".backup '$($destDb -replace "'","''")'"
if ($LASTEXITCODE -ne 0) { throw "sqlite .backup failed (exit $LASTEXITCODE)" }

Write-Host "[2/4] verify: PRAGMA quick_check + row counts vs source"
$check = & $sqlite $destDb 'PRAGMA quick_check;'
if ($check -ne 'ok') { throw "quick_check on backup returned: $check" }
foreach ($t in 'Experiments', 'TradeResults', 'BacktestRuns') {
    $srcN = & $sqlite $SrcDb "SELECT COUNT(*) FROM $t;"
    $dstN = & $sqlite $destDb "SELECT COUNT(*) FROM $t;"
    if ($srcN -ne $dstN) { throw "row-count mismatch on ${t}: source=$srcN backup=$dstN" }
    Write-Host ("  {0}: {1} rows OK" -f $t, $dstN)
}

if (-not $SkipArchive) {
    Write-Host "[3/4] robocopy Dukascopy archive -> $destRoot\backfill"
    robocopy $SrcArchive (Join-Path $destRoot 'backfill') /E /R:2 /W:2 /NP /NFL /NDL
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)" }
    $srcCount = (Get-ChildItem $SrcArchive -Recurse -File).Count
    $dstCount = (Get-ChildItem (Join-Path $destRoot 'backfill') -Recurse -File).Count
    if ($dstCount -lt $srcCount) { throw "archive file-count mismatch: source=$srcCount backup=$dstCount" }
    Write-Host "  $dstCount files OK"
} else { Write-Host "[3/4] archive skipped (-SkipArchive)" }

Write-Host "[4/4] manifest"
$manifest = [ordered]@{
    createdUtc = (Get-Date).ToUniversalTime().ToString('o')
    sourceDb = $SrcDb
    dbBytes = (Get-Item $destDb).Length
    quickCheck = 'ok'
}
if ($Hash) {
    Write-Host "  hashing (slow)..."
    $manifest.dbSha256 = (Get-FileHash $destDb -Algorithm SHA256).Hash
}
$manifest | ConvertTo-Json | Out-File -Encoding utf8 (Join-Path $destRoot 'MANIFEST.json')
Write-Host "DONE: $destRoot"
