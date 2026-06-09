# Start Web app, trigger replay backtest, collect metrics
$ErrorActionPreference = "Stop"

# Kill any existing dotnet processes on port 5000
$existing = netstat -ano 2>$null | findstr ":5000.*LISTENING"
if ($existing) { Write-Host "Warning: port 5000 is in use" }

$webDir = "src\TradingEngine.Web"
Write-Host "Starting Web app..."
$proc = Start-Process dotnet -ArgumentList "run --project $webDir --environment Development --urls http://localhost:5000" -PassThru -NoNewWindow

# Wait for health endpoint
Write-Host "Waiting for server to start..."
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 2
    try {
        $health = Invoke-WebRequest "http://localhost:5000/health" -UseBasicParsing -TimeoutSec 2 2>$null
        if ($health.StatusCode -eq 200) {
            Write-Host "Server healthy at attempt $i"
            break
        }
    } catch { }
}
if ($i -ge 30) { Write-Host "ERROR: Server did not start within 60s"; $proc.Kill($true); exit 1 }

# Trigger backtest
Write-Host "Triggering backtest..."
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

# Poll until complete
Write-Host "Polling status..."
$done = $false
for ($i = 0; $i -lt 60 -and -not $done; $i++) {
    Start-Sleep -Seconds 5
    $status = (Invoke-WebRequest "http://localhost:5000/api/backtest/$runId/status" -UseBasicParsing).Content | ConvertFrom-Json
    Write-Host "Status: $($status.status)"
    if ($status.status -in @("completed","failed")) { $done = $true; $finalStatus = $status }
}
if (-not $done) { Write-Host "WARNING: Backtest timed out after 5 min" }

# Query DB
Write-Host ""
Write-Host "=== DB Metrics ==="
Write-Host "BarEvaluations total:"; sqlite3 data\trading.db "SELECT COUNT(*) FROM BarEvaluations WHERE RunId='$runId';"
Write-Host "SignalsFired:"; sqlite3 data\trading.db "SELECT COUNT(*) FROM BarEvaluations WHERE RunId='$runId' AND SignalFired=1;"
Write-Host "Top rejection reasons:"; sqlite3 data\trading.db "SELECT Reason, COUNT(*) as cnt FROM BarEvaluations WHERE RunId='$runId' AND SignalFired=0 GROUP BY Reason ORDER BY cnt DESC LIMIT 5;"
Write-Host "TradesOpened:"; sqlite3 data\trading.db "SELECT COUNT(*) FROM TradeResults WHERE RunId='$runId';"
Write-Host "Per-strategy breakdown:"; sqlite3 data\trading.db "SELECT StrategyId, COUNT(*) as TotalBars, SUM(SignalFired) as Signals FROM BarEvaluations WHERE RunId='$runId' GROUP BY StrategyId;"

Write-Host ""
Write-Host "RunId: $runId"

# Kill the web server
Write-Host "Stopping server..."
$proc | Stop-Process -Force 2>$null
Write-Host "Done."
