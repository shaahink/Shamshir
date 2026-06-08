# Iteration 13 — Sign-Off & Handover

**Branch**: `iter/13-observability` (based on `iter/12-replay-ui-wire`)
**Completed**: 2026-06-09

---

## Summary

Observability pass: log level fix, structured progress events, per-strategy breakdown,
color-coded live UI, and shutdown drain fix for BarEvaluationHandler.

---

## Files changed

| Phase | File | Change |
|-------|------|--------|
| A | `src/TradingEngine.Host/EngineWorker.cs` | BAR_EVAL `LogInformation`→`LogDebug`; `IProgress<BacktestProgressEvent>?` param; BAR/SIGNAL/ORDER progress callbacks |
| A | `src/TradingEngine.Host/BacktestProgressEvent.cs` | New record: `(RunId, EventType, Message, TimestampUtc)` |
| A | `src/TradingEngine.Host/BarEvaluationHandler.cs` | `DisposeAsync` drains remaining channel events before dispose (DESIGN-07) |
| B | `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | `PushProgressEvent` method; `Progress<BacktestProgressEvent>` registered in inner host DI; explicit EngineWorker factory with progress param |
| C | `src/TradingEngine.Web/Services/IBacktestQueryService.cs` | `StrategyPerformance` record + `GetStrategyBreakdownAsync` |
| C | `src/TradingEngine.Web/Services/BacktestQueryService.cs` | Breakdown query: bar evaluations grouped by strategy + rejection reasons |
| C | `src/TradingEngine.Web/Pages/Backtests/Detail.cshtml` | Per-strategy table with rejection reasons |
| C | `src/TradingEngine.Web/Pages/Backtests/Detail.cshtml.cs` | Loads `StrategyBreakdown` on page load |
| D | `src/TradingEngine.Web/Pages/Backtests/Progress.cshtml` | Color-coded events (grey BAR, blue SIGNAL, green ORDER, orange TRADE); live counters |
| — | `src/TradingEngine.Web/appsettings.Development.json` | CTrader credentials (seankiaa) |
| — | `src/TradingEngine.Host/appsettings.json` | ActiveStrategyIds: mean-reversion |

---

## Issues fixed

| Issue | Root cause | Fix |
|-------|-----------|-----|
| STD-03 | BAR_EVAL logged at Information, flooding logs | Changed to LogDebug |
| DESIGN-07 | BarEvaluationHandler.DisposeAsync silently dropped channel events | Drain remaining events to DB before disposal |
| OBS-01/02/03/05 | No live progress, no structured events, no per-strategy breakdown | IProgress callbacks, structured SSE, strategy table on Detail |

---

## Verification results (automated)

| Check | Result |
|-------|--------|
| `dotnet build --no-incremental` | 0 errors |
| Unit tests (87) | 87/87 |
| Integration tests (15) | 15/15 |
| `ReplayBacktest_FullPipeline_ProducesBarEvaluations` (gate) | PASS (10s) |

## Verification results (manual — user to complete)

| Check | Status |
|-------|--------|
| cTrader backtest from UI (UseForBacktest: true, EURUSD H1 1-month) | |
| SIGNAL events in blue on Progress page | |
| Strategy breakdown table on Detail page | |
| Rejection reasons displayed | |

### How to run the manual verification

```powershell
# Terminal 1: Start the web app
dotnet run --project src/TradingEngine.Web --environment Development

# Browser: navigate to https://localhost:5001/backtests/run
# Select: Symbol=Other (type EURUSD), Period=H1, Start/End = 1 month
# Click Run Backtest
# Watch Progress page: confirm blue SIGNAL lines appear
# After completion, go to Detail page: check strategy breakdown table
```

After verification, fill in the numbers below:

| Metric | Value |
|--------|-------|
| TotalBarsEvaluated | |
| SignalsFired | |
| TradesOpened | |
| Top rejection reason | |

If 0 SIGNAL events appear, document the rejection reason verbatim from the DB:

```sql
SELECT Reason, COUNT(*) as cnt FROM BarEvaluations
WHERE RunId = '<your-run-id>' AND SignalFired = 0
GROUP BY Reason ORDER BY cnt DESC LIMIT 1;
```

---

## Key design decisions

### 1. EngineWorker explicit factory
`IProgress<BacktestProgressEvent>?` is an optional parameter on `EngineWorker`. .NET DI does
NOT inject optional parameters automatically. The inner host's DI registers `EngineWorker`
via an explicit factory that passes `sp.GetRequiredService<IProgress<BacktestProgressEvent>>()`
for the `progress` parameter.

### 2. Structured SSE via PushProgressEvent
The progress callback sends `{ eventType, message }` JSON directly to the SSE writer (separate
from the existing `{ line }` format for backwards-compatible log lines). The Progress page JS
checks `data.eventType` first, then falls back to `data.line`.

### 3. DESIGN-07 fix: final drain on dispose
After cancelling the flush CTS and awaiting `_flushTask`, `DisposeAsync` now drains any
remaining events from the channel via `TryRead` and flushes them to the DB synchronously.
This ensures small backtests (< 3s) still have `BarEvaluations` in the DB.

### 4. Strategy breakdown query
Groups `BarEvaluations` by strategy + reason + signal flag, joins with `TradeResults` per
strategy. Shows total bars, signals fired, trades opened, win/loss, win rate, and top 5
rejection reasons per strategy.
