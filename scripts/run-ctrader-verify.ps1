# Start Web app, trigger cTrader backtest, collect metrics
$ErrorActionPreference = "Continue"

$webDir = "src\TradingEngine.Web"
Write-Host "Starting Web app..."
$proc = Start-Process dotnet -ArgumentList "run --project $webDir --environment Development --urls http://localhost:5000" -PassThru -NoNewWindow

Write-Host "Waiting for server..."
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 2
    try {
        $health = Invoke-WebRequest "http://localhost:5000/health" -UseBasicParsing -TimeoutSec 2 2>$null
        if ($health.StatusCode -eq 200) { Write-Host "Healthy at attempt $i"; break }
    } catch { }
}
if ($i -ge 30) { Write-Host "ERROR: server timeout"; $proc | Stop-Process -Force 2>$null; exit 1 }

# Trigger cTrader backtest
Write-Host "Triggering cTrader backtest..."
$body = @{
    symbol = "EURUSD"
    period = "h1"
    start  = "2024-01-15T00:00:00"
    end    = "2024-02-15T00:00:00"
    balance = 100000
    commissionPerMillion = 30
    spreadPips = 1
} | ConvertTo-Json

$resp = Invoke-WebRequest -Uri "http://localhost:5000/api/backtest/start" `
    -Method POST -ContentType "application/json" -Body $body -UseBasicParsing
$runId = ($resp.Content | ConvertFrom-Json).runId
Write-Host "RunId: $runId"

# cTrader path takes longer - poll up to 15 min
Write-Host "Polling (ctrader-cli may take several minutes)..."
$done = $false
for ($i = 0; $i -lt 180 -and -not $done; $i++) {
    Start-Sleep -Seconds 5
    $status = (Invoke-WebRequest "http://localhost:5000/api/backtest/$runId/status" -UseBasicParsing).Content | ConvertFrom-Json
    Write-Host "  [$($i+1)] Status: $($status.status)"
    if ($status.status -in @("completed","failed")) { $done = $true; $finalStatus = $status }
}
if (-not $done) { Write-Host "WARNING: timed out after 15 min" }

# Query DB
Write-Host "`n=== DB Metrics ==="
Write-Host "Trades:"; sqlite3 data\trading.db "SELECT COUNT(*) FROM TradeResults WHERE RunId='$runId';"
Write-Host "BarEvaluations:"; sqlite3 data\trading.db "SELECT COUNT(*) FROM BarEvaluations WHERE RunId='$runId';"
Write-Host "Signals:"; sqlite3 data\trading.db "SELECT COUNT(*) FROM BarEvaluations WHERE RunId='$runId' AND SignalFired=1;"
Write-Host "Per-strategy:"; sqlite3 data\trading.db "SELECT StrategyId, COUNT(*) as Bars, SUM(SignalFired) as Signals FROM BarEvaluations WHERE RunId='$runId' GROUP BY StrategyId;"
Write-Host "Top rejections:"; sqlite3 data\trading.db "SELECT Reason, COUNT(*) as cnt FROM BarEvaluations WHERE RunId='$runId' AND SignalFired=0 GROUP BY Reason ORDER BY cnt DESC LIMIT 3;"

Write-Host "`nRunId: $runId"
Write-Host "Status: $($finalStatus.status)"
if ($finalStatus.error) { Write-Host "Error: $($finalStatus.error)" }

$proc | Stop-Process -Force 2>$null
Write-Host "Done."
