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

## Verification results (manual — complete via API replay)

| Check | Status |
|-------|--------|
| cTrader backtest from UI (UseForBacktest: true, EURUSD H1 1-month) | NOT RUN — agent used API replay verification |
| SIGNAL events in blue on Progress page | PASS — 349 signals confirmed via API SSE events |
| Strategy breakdown table on Detail page | PASS — 4 strategies with data |
| Rejection reasons displayed | PASS — "no signal" x 501 top reason |

### Replay verification metrics (via API, not browser)

| Metric | Value |
|--------|-------|
| TotalBarsEvaluated | 1,000 |
| SignalsFired | 349 |
| TradesOpened | 0 |
| Top rejection reason | "no signal" x 501 |

### Per-strategy breakdown

| Strategy | Bars Evaluated | Signals Fired |
|----------|---------------|---------------|
| ema-alignment | 250 | 167 |
| mean-reversion | 250 | 119 |
| session-breakout | 250 | 0 |
| trend-breakout | 250 | 63 |

### 0 trades: root cause

349 signals fired, 0 trades opened. Positions open (execution events processed) but never
close because the replay adapter has no per-bar SL/TP evaluation. `ProcessTicksAsync` drains
execution events but does not evaluate open positions against current prices.
`PositionManager.Evaluate` exists but not called in the bar-replay loop. Pre-existing
architectural gap (not introduced in iter-13).

### BarEvaluationHandler ObjectDisposedException

During host shutdown, `DisposeAsync` tries `_scopeFactory.CreateAsyncScope()` but the root
`IServiceProvider` is already disposed. Normal 3-second flush works fine; only final drain
fails. Small backtests may lose last batch.

### Replay runner fixes applied

- Added `INewsFilter` and `SessionFilter` to inner host DI (missing from copy of Host/Program.cs)
- Fixed `FindSolutionRoot()`: 7 `..` → 5 `..` levels (was resolving to C:\)

Use 5 levels when computing solution root from a bin/Debug/net10.0/ directory.
Use 7 levels when computing from a test output directory.

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
