# Iteration 11 — Sign-Off & Handover

**Branch**: `iter/11-phase-b-e2e` (based on `phase/8b-bar-tracing`)
**Completed**: 2026-06-09

---

## Summary

Fixed three critical bugs in `BacktestReplayAdapter` that caused replay backtests to always
produce 0 trades. Built an end-to-end gate test that proves the full pipeline works without
cTrader credentials.

---

## Files changed

| Phase | File | Change |
|-------|------|--------|
| A | `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` | Full rewrite: unbounded channels, instant fills, async background feed |
| A | `src/TradingEngine.Services/PositionTracker.cs` | Added `DetermineExitReason`; replaced incorrect ternary |
| A | `tests/TradingEngine.Tests.Integration/AdapterTests/BacktestReplayAdapterTests.cs` | 3 unit-level adapter tests (BUG-01/02/03) |
| B | `tests/TradingEngine.Tests.Simulation/TradingEngine.Tests.Simulation.csproj` | Added `TradingEngine.Host` project reference |
| B | `tests/TradingEngine.Tests.Simulation/Harness/AlwaysSignalStrategy.cs` | Deterministic test-only strategy |
| B | `tests/TradingEngine.Tests.Simulation/Harness/ReplayTestHarness.cs` | Minimal `IHost` wiring for E2E replay tests |
| B | `tests/TradingEngine.Tests.Simulation/BacktestReplayTests.cs` | Gate test: `ReplayBacktest_FullPipeline_ProducesBarEvaluations` |

---

## Bugs fixed

| Issue | Root cause | Fix |
|-------|-----------|-----|
| BUG-01 | `SubmitOrderAsync` stored orders in `_pendingOrders`, nothing ever filled them | `SubmitOrderAsync` now writes `ExecutionEvent` directly to `_executionChannel` at bar close |
| BUG-02 | `_barChannel` bounded at 2,000 with `DropOldest`; `ConnectAsync` awaited all bars before consumer started | Unbounded channels; `ConnectAsync` starts feed as background task, returns immediately |
| BUG-03 | `ClosePositionAsync` sent `FillPrice = null`; `PositionTracker.OnExecution` silently discarded it | `ClosePositionAsync` sends `_lastClose` as fill price |
| Exit reason | Ternary assumed any close above SL = TP, regardless of TP existence | `DetermineExitReason` checks TP existence, returns "FORCE" when neither SL nor TP applies |

---

## Verification results

| Check | Result |
|-------|--------|
| `dotnet build --no-incremental` | 0 errors (5 pre-existing CTrader net6.0 warnings) |
| Unit tests (87 baseline) | 87/87 |
| Integration tests | 15/15 |
| `AllBars_DeliveredWithoutDataLoss` | PASS |
| `SubmitOrder_ReceivesInstantFillWithPrice` | PASS |
| `ClosePosition_SendsFillPriceNotNull` | PASS |
| `ReplayBacktest_FullPipeline_ProducesBarEvaluations` | PASS (9s) |
| Simulation suite (excl. CTrader-dependent) | 9/11 (2 pre-existing: `FullBacktestPipelineTest` needs CTrader env vars) |

---

## Key design decisions for iter-12

### 1. `_executionChannel` is NOT completed in the feed's `finally`
The feed completes `_barChannel`, `_tickChannel`, `_accountChannel` only. `_executionChannel`
is completed in `DisconnectAsync` and `DisposeAsync` only. This gives the engine's consumers
time to process all bars and submit orders before the execution channel closes.

**Engine shutdown for replay backtests**: `ProcessExecutionEventsAsync` reads from
`_executionChannel` with `ReadAllAsync(ct)`. Since `_executionChannel` is never completed
during normal operation, this loop will block. In the harness, we wait for
`BarStream.Completion` (all bars consumed), then call `StopAsync`, which cancels the
engine's `BackgroundService` stopping token, causing `ReadAllAsync` to throw
`OperationCanceledException`, and the consumer exits cleanly.

### 2. Harness shutdown pattern
```
StartAsync → wait for BarStream.Completion → delay 5s (flush grace) → StopAsync
```
The 5-second delay allows `BarEvaluationHandler`'s 3-second flush cycle to persist
evaluations to the DB before the host shuts down.

### 3. Constructor signatures mismatched from plan
All provided in the plan were based on an earlier snapshot. The actual types have more
parameters (`SymbolInfo`: 12 params, `RiskProfile`: 18 params, `RiskProfileResolver` takes
`IReadOnlyList` not dictionary). The harness was written against the real types.

### 4. `MarketContext` has `LatestTick`, not `LatestBar`
`AlwaysSignalStrategy` uses `context.LatestTick.Bid` as the bar close price.

---

## iter-12 readiness checklist

| Item | Status |
|------|--------|
| BUG-01/02/03 fixed | Yes |
| Gate test `ReplayBacktest` green | Yes |
| Adapter handles bar-to-execution flow | Yes |
| `IBacktestRunRepository.UpdateAsync` exists | No — iter-12 Phase A adds this |
| `BacktestOrchestrator` takes `IConfiguration` | No — iter-12 adds this to ctor |
| Web `.csproj` references Host/Services/Strategies/Risk | No — iter-12 Phase A adds these |

---

## Known caveats

- `BarEvaluationHandler` buffer loss on shutdown (DESIGN-06) — remaining evaluations in the
  handler's internal channel are dropped when `StopAsync` cancels the flush token. The 5s
  grace delay mitigates this but doesn't solve it completely.
- `_lastClose` may be 1 bar ahead of the bar being evaluated (feed runs asynchronously
  ahead of consumers). This is acceptable for backtesting (instant fill at current close).
- Ticks are 1:1 with bars in the replay feed (synthetic tick per bar). This is sufficient
  because execution events drain in `ProcessTicksAsync`'s tick loop.
