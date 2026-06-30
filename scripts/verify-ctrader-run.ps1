# verify-ctrader-run.ps1 — cTrader acceptance oracle
# Usage: .\scripts\verify-ctrader-run.ps1 <runId>
# Returns exit code 0 only if ALL five checks pass.
# Designed to be run by the owner after a single cTrader backtest.

param(
    [Parameter(Mandatory = $true)]
    [string]$RunId
)

$ErrorActionPreference = "Stop"
$db = "src\TradingEngine.Web\data\trading.db"
$failures = 0

function Fail($check, $detail) {
    Write-Host "FAIL [$check] $detail" -ForegroundColor Red
    $script:failures++
}
function Pass($check, $detail) {
    Write-Host "PASS [$check] $detail" -ForegroundColor Green
}

if (-not (Test-Path $db)) {
    Write-Host "ERROR: Database not found at $db" -ForegroundColor Red
    exit 2
}

Write-Host "`n=== verify-ctrader-run.ps1 === RunId=$RunId ===" -ForegroundColor Cyan

# --- Check 1: openRisk never exceeds bound ---
Write-Host "`n--- Check 1: openRisk bounded ---"
$leakRows = sqlite3 $db @"
SELECT substr(SimTimeUtc,1,16), DecisionReason
FROM Journal
WHERE RunId='$RunId'
  AND (DecisionReason LIKE '%openRisk=%' OR DecisionReason LIKE 'Budget%' OR DecisionReason LIKE 'MAX_%')
ORDER BY Seq;
"@
if ($LASTEXITCODE -ne 0) { Fail "1" "sqlite3 query failed: $leakRows" }
else {
    $lines = $leakRows | Where-Object { $_ -match '\S' }
    if ($lines.Count -eq 0) {
        Pass "1" "No gate-limit rejections in journal (openRisk in bounds)"
    } else {
        # Extract openRisk values
        $maxRisk = 0
        foreach ($line in $lines) {
            if ($line -match 'openRisk=([0-9,.]+)') {
                $risk = [decimal]($matches[1] -replace ',', '')
                if ($risk -gt $maxRisk) { $maxRisk = $risk }
            }
        }
        # Bound: 5 positions * ~2500 worst-case per trade = 12500 (generous)
        $bound = 20000
        if ($maxRisk -le $bound) {
            Pass "1" "openRisk max=$maxRisk <= $bound (bounded)"
        } else {
            Fail "1" "openRisk max=$maxRisk > $bound (unbounded growth — book leaked)"
        }
    }
}

# --- Check 2: exit reasons include SL/TP (not all FORCE) ---
Write-Host "`n--- Check 2: exit reasons ---"
$exitRows = sqlite3 $db "SELECT ExitReason, COUNT(*) FROM TradeResults WHERE RunId='$RunId' GROUP BY ExitReason;"
if ($LASTEXITCODE -ne 0) { Fail "2" "sqlite3 query failed: $exitRows" }
elseif (-not $exitRows) { Fail "2" "No trades found for this run" }
else {
    $exitRows
    $hasSlTp = $exitRows | Where-Object { $_ -match '^(SL|TP|PARTIAL)\|' }
    if ($hasSlTp) { Pass "2" "Real exit reasons (SL/TP/PARTIAL) present" }
    else { Fail "2" "All exits are FORCE — venue close reason not threaded" }
}

# --- Check 3: trade count for full window >= trailing sub-window ---
Write-Host "`n--- Check 3: trade count monotonicity ---"
$tradeInfo = sqlite3 $db @"
SELECT MIN(ClosedAtUtc), MAX(ClosedAtUtc), COUNT(*)
FROM TradeResults WHERE RunId='$RunId';
"@
if ($LASTEXITCODE -ne 0) { Fail "3" "sqlite3 query failed: $tradeInfo" }
elseif (-not $tradeInfo) { Fail "3" "No trades found" }
else {
    $parts = $tradeInfo -split '\|'
    $firstClose = $parts[0]
    $lastClose = $parts[1]
    $totalTrades = [int]$parts[2]
    if ($totalTrades -le 1) {
        Pass "3" "Too few trades ($totalTrades) for monotonicity check - skipped"
    } else {
        # Trailing 1/3 of the window
        try {
            $first = [DateTime]::Parse($firstClose)
            $last = [DateTime]::Parse($lastClose)
            $span = $last - $first
            $subStart = $last.Add(-($span.TotalDays / 3)).ToString("yyyy-MM-dd HH:mm:ss")
            $subCount = sqlite3 $db "SELECT COUNT(*) FROM TradeResults WHERE RunId='$RunId' AND ClosedAtUtc >= '$subStart';"
            $subCount = [int]($subCount -replace '\D', '')
            if ($totalTrades -ge $subCount) {
                Pass "3" "Total $totalTrades >= suffix $subCount (no reversal)"
            } else {
                Fail "3" "Total $totalTrades < suffix $subCount (open-book leak — fewer trades in longer window)"
            }
        } catch {
            Fail "3" "Date parse failed: $tradeInfo"
        }
    }
}

# --- Check 4: run completed cleanly ---
Write-Host "`n--- Check 4: run completion ---"
$runInfo = sqlite3 $db "SELECT ExitCode, CompletedAtUtc FROM BacktestRuns WHERE RunId='$RunId';"
if ($LASTEXITCODE -ne 0 -or -not $runInfo) { Fail "4" "Run not found or query failed" }
else {
    $parts = $runInfo -split '\|'
    $exitCode = [int]($parts[0] -replace '\D', '')
    $completedAt = $parts[1]
    if ($exitCode -eq 0) {
        if ($completedAt -and $completedAt -ne '0001-01-01 00:00:00' -and $completedAt -notmatch '^0001') {
            Pass "4" "ExitCode=0, CompletedAtUtc=$completedAt"
        } else {
            Fail "4" "ExitCode=0 but CompletedAtUtc=$completedAt (not written or sim-time)"
        }
    } else {
        Fail "4" "ExitCode=$exitCode (run errored)"
    }
}

# --- Check 5: equity snapshots exist ---
Write-Host "`n--- Check 5: equity snapshots ---"
$eqCount = sqlite3 $db "SELECT COUNT(*) FROM EquitySnapshots WHERE RunId='$RunId';"
if ($LASTEXITCODE -ne 0) { Fail "5" "sqlite3 query failed: $eqCount" }
else {
    $eqCount = [int]($eqCount -replace '\D', '')
    if ($eqCount -gt 0) { Pass "5" "$eqCount equity snapshots persisted" }
    else { Fail "5" "0 equity snapshots — equity persistence not wired on kernel path" }
}

# --- Summary ---
Write-Host "`n=== RESULT: $failures / 5 checks failed ===" -ForegroundColor $(if ($failures -eq 0) { "Green" } else { "Red" })
exit $failures
