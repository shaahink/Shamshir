# Shamshir — Backtest Smoke Test Procedure

> Run this on a machine with cTrader installed to verify the full engine + cBot + CLI pipeline.

## Prerequisites

- cTrader desktop installed (provides `ctrader-cli.exe`)
- cTrader credentials (CtID, password file)
- `cAlgo.API` NuGet restored (done by `dotnet restore`)

## Step 1 — Build everything

```powershell
dotnet build TradingEngine.sln --configuration Release
```

Verify:
- `src/TradingEngine.Adapters.CTrader/bin/Release/net6.0/src.algo` exists
- No build errors

## Step 2 — Verify cBot metadata

```powershell
ctrader-cli metadata "src/TradingEngine.Adapters.CTrader/bin/Release/net6.0/src.algo"
```

Should show `Pipe Name` parameter with default `trading-engine`.

## Step 3 — Start the engine (Live mode)

In terminal 1:
```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/TradingEngine.Host -- --mode Live
```

The engine starts and calls `NamedPipeBrokerAdapter.ConnectAsync()`. It blocks waiting for the cBot to connect. **ctrader-cli must start within 30 seconds** or the engine's connection retry timer expires.

## Step 4 — Run a backtest via ctrader-cli

In terminal 2 (within 30s of step 3):
```powershell
ctrader-cli backtest "src/TradingEngine.Adapters.CTrader/bin/Release/net6.0/src.algo" ^
  --start=01/01/2024 --end=05/01/2024 ^
  --symbol=EURUSD --period=h1 --balance=100000 ^
  --commission=30 --spread=1 ^
  --data-mode=m1 ^
  --PipeName=trading-engine ^
  --exit-on-stop ^
  --ctid=your-ctid@email.com ^
  --pwd-file="C:\path\to\password.pwd" ^
  --account=your-account-number
```

The CLI:
1. Downloads market data from cTrader servers
2. Starts the cBot in backtest mode
3. cBot connects to the engine's pipe
4. cBot sends ticks/bars via pipe
5. Engine processes signals and sends orders via pipe
6. cBot executes orders via cTrader API simulation
7. On completion, CLI writes `report.json` and `events.json`

## Step 5 — View results in web UI

```powershell
dotnet run --project src/TradingEngine.Web
```

Open `http://localhost:5000`. The `/backtests` page should show the completed run.

## Timing notes

- Engine starts → `NamedPipeBrokerAdapter.ConnectAsync()` → waits for client — **30s timeout**
- ctrader-cli must start within this window
- If the engine times out, restart it before re-running the CLI
- The CLI takes 1–5 minutes depending on date range and data download

## File locations

| Artifact | Path |
|---|---|
| `.algo` cBot binary | `src/TradingEngine.Adapters.CTrader/bin/Release/net6.0/src.algo` |
| Backtest SQLite DB | `data/trading.db` |
| CLI report JSON | `%TEMP%\shamshir-backtest\{runId}\report.json` |
| cTrader data cache | `--data-dir` parameter (configurable) |
