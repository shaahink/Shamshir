# cTrader Quickstart — Verify the cTrader Backtest Path

**For:** Future agents and maintainers who need to confirm the cTrader backtest path works.
**Branch:** `iter/parity-pipeline`
**Prerequisite:** P7.2 is DONE — this doc captures the verified working path.

## Credentials (pre-verified)

All credentials live in `src/TradingEngine.Web/appsettings.Development.json` under `CTrader`:

| Key | Value | Source |
|-----|-------|--------|
| `CtId` | `seankiaa` | `CTrader:CtId` in appsettings |
| `Account` | `5834367` | `CTrader:Account` in appsettings |
| `PwdFile` | `C:\Users\shahi\Documents\ctrader.pwd` | `CTrader:PwdFile` in appsettings |
| CLI binary | Resolved automatically | `CTraderCliLocator` scans `%LOCALAPPDATA%\Spotware\cTrader\[hash]\` |

The "needs creds" myth from P0-P2 was a deadlock bug (B1-B3, now fixed). Credentials are accessible.

## Pre-requisites

1. Build the cBot:
   ```powershell
   dotnet build src/TradingEngine.Adapters.CTrader
   ```

2. Build the web app:
   ```powershell
   dotnet build TradingEngine.slnx
   ```

3. Start the web app (from `src/TradingEngine.Web`):
   ```powershell
   cd src/TradingEngine.Web
   $env:ASPNETCORE_ENVIRONMENT = "Development"
   dotnet run
   ```
   Wait for `Now listening on: http://localhost:5134`.

## Quick Verification (no new run needed)

The DB already has a proven cTrader run. Query it directly:

```powershell
c:\adb\sqlite3.exe src/TradingEngine.Web/data/trading.db ^
  "SELECT RunId, Venue, ExitCode, TotalTrades FROM BacktestRuns WHERE RunId='77e37dee';"
```

Confirmed run: `77e37dee` — ExitCode=0, TotalTrades=1, EURUSD Long, NetPnL=312.31.

## Start a New cTrader Backtest

1. Verify the app is listening:
   ```powershell
   Invoke-RestMethod -Uri "http://localhost:5134/api/system/health" -Method Get -TimeoutSec 5
   ```

2. Start the backtest (3-day window, fast turnaround):
   ```powershell
   $body = '{"start":"2026-01-15","end":"2026-01-18","symbols":["EURUSD"],"periods":["H1"],"balance":100000,"venue":"ctrader"}'
    Invoke-RestMethod -Uri "http://localhost:5134/api/runs" -Method Post -Body $body -ContentType "application/json"
   ```
   Response contains `runId`. Expected turnaround: 60-120 seconds for a short window.

3. Poll until terminal:
   ```powershell
   # Replace {runId} with the actual run ID from step 2
   $runId = "{runId}"
   do {
     Start-Sleep -Seconds 5
      $status = Invoke-RestMethod -Uri "http://localhost:5134/api/runs/$runId" -Method Get -TimeoutSec 10
     Write-Output "$(Get-Date -Format 'HH:mm:ss') status=$($status.status) trades=$($status.totalTrades) exitCode=$($status.exitCode)"
   } while ($status.status -eq 'running')
   ```

4. Terminal statuses: `completed`, `completed-with-warnings`, `failed`.

5. Verify in DB:
   ```powershell
   c:\adb\sqlite3.exe src/TradingEngine.Web/data/trading.db ^
     "SELECT RunId, Venue, ExitCode, TotalTrades, ErrorMessage FROM BacktestRuns WHERE RunId='$runId';"
   ```

## Troubleshooting

| Symptom | Check |
|---------|-------|
| Run hangs at "running" | The 30-minute linked CTS timeout catches hung `BacktestAsync` calls. If exceeded, status flips to `failed`. |
| Run stays "running" after CLI exit | The 30-second `BarStream.Completion` safety timeout in `RunEngineNetMqAsync`'s `finally` block forces `DisconnectAsync()`. |
| `ExitCode=-1` | cTrader CLI crashed or was killed. Check `BacktestRun.ErrorMessage` in DB. |
| `TotalTrades=0` | The trade persistence barrier surfaces `TRADES_UNRECONSTRUCTABLE` if closes arrive as raw OrderFilled events (no PublishTradeClosed). Status becomes `completed-with-warnings`. |
| App won't start (port 5134 in use) | Kill the app's dotnet process by PID. Find it with `Get-Process dotnet` and stop the one running from `TradingEngine.Web`. |
| Build lock (MSB3021) | Kill all dotnet processes before building: `Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force` |

## Architecture: How It Works

```
Web App (localhost:5134)
  └─ POST /api/runs {"venue":"ctrader",...}
       └─ BacktestOrchestrator.RunAsync()
            └─ CTraderCli.BacktestAsync()              ← runs %LOCALAPPDATA%\Spotware\cTrader\[hash]\ctrader-cli.exe
            └─ cTrader CLI starts the cBot (.algo)     ← built from src/TradingEngine.Adapters.CTrader
            └─ cBot connects via NetMQ transport
            └─ CTraderBrokerAdapter.ReadRouterLoop()    ← reads bars, ticks, executions
            └─ EngineReducer processes bars → decisions
            └─ When CLI exits, channels complete naturally (B2 fix)
            └─ Orchestrator persists results to trading.db
```

Key fixes that made this work:
- **B2 (deadlock):** `ReadRouterLoop` completes bar/exec channels in a `finally` block so `BarStream.Completion` fires without needing `DisconnectAsync` first.
- **B2 safety net:** 30-second timeout on `BarStream.Completion` await, forces `DisconnectAsync()` on timeout.
- **30-min CTS:** Linked cancellation token catches hung CLI calls.

## Run the Gate Battery

After any cTrader code change, verify nothing regressed:
```powershell
dotnet build TradingEngine.slnx
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration
dotnet test tests/TradingEngine.Tests.Simulation --filter "RequiresCTrader!=true&Category!=E2E&Category!=Slow&Category!=NetMQ"
```
Golden: `git diff --stat **/*golden*.json` — must be empty.
