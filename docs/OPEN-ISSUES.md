# Shamshir — Open Issues

**Updated**: 2026-06-19 (iter-35 finish — Parts A+B+C+D+E complete, final E2E 13/13)
**Branch**: `iter/35-kernel-finish-ab`
**Total open**: ~20 remaining (55+ resolved across iter-35 + finish session)

> **iter-35 complete.** The kernel is the **single production authority** for: order gate, SL/TP detection,
> breach watchdog, drawdown evaluation, protection exit, position sizing, and governor logic.
> E2E infrastructure: Playwright headless Chromium, temp DB + seed bars (2000 EURUSD H1), replay
> backtest produces 16 real trades, 13/13 browser checks pass with verified data.
> All suites: build 0 errors, 209 unit + 18 golden + 4 arch + 13/13 E2E.

Fixed items → `docs/RESOLVED-ISSUES.md`. Roadmap → `docs/NEXT-STEPS.md`.
Full audit narrative + system model → `docs/reference/SYSTEM-AUDIT.md`.
Iter-35 handover → `docs/iterations/iter-35/HANDOVER.md`.

---

## Critical (14) — Correctness-breaking, fix immediately

### C1 — cTrader limit orders always execute as market
**Severity**: Critical — ✅ **FIXED iter-35 finish** — cBot now reads `orderType`/`limitPrice`, routes "Limit" orders through `PlaceLimitOrder` instead of `ExecuteMarketOrder`.
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:298-345`

### C2 — cTrader has no `cancel_order` handler
**Severity**: Critical — ✅ **FIXED iter-35 finish** — `ExecuteCancelOrder` handler added, dispatches to `ClosePosition`.
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:236-266`

### C3 — Trailing max-DD floor uses `equity.Equity` instead of `equity.PeakEquity`
**Severity**: Critical — ✅ **FIXED iter-35** (kernel `PreTradeGate` → `DrawdownState.GetMaxDrawdownFloor`). Old `RiskManager.cs` still has the bug but kernel gate is authoritative going forward.
**File**: `src/TradingEngine.Risk/RiskManager.cs:186-187`
```csharp
var drawdownBase = Drawdown.DrawdownType == "Trailing" ? equity.Equity : equity.Balance;
```
For trailing mode, should be `equity.PeakEquity`. As equity drops, the projected floor also drops, making the gate artificially permissive. Same bug in `RiskGate.cs:39`.

### C4 — MaxDD protection mode never auto-exits
**Severity**: Critical — ✅ **FIXED iter-35** (kernel `ProtectionState.ClearsOn` + `Kernel.DecideReset`). Old `RiskManager.OnDailyReset` still has the bug but kernel path is authoritative going forward.
**File**: `src/TradingEngine.Risk/RiskManager.cs:299-307`
`OnDailyReset()` only clears `ProtectionCause.DailyDrawdown`. MaxDD-caused protection stays forever. `PropFirmRuleSet.ProtectionResetPolicy` is defined but **never read by any code**.

### C5 — SimulatedBrokerAdapter AccountUpdate param swap (Equity=0)
**Severity**: Critical — ✅ **FIXED iter-35** (all 3 sites: `(balance, balance, 0)` instead of `(balance, 0, balance)`)
**File**: `src/TradingEngine.Infrastructure/Venues/Simulated/SimulatedBrokerAdapter.cs:165-166,279-280,329-330`
```csharp
new AccountUpdate(_currentBalance, 0m, _currentBalance, now)
```
Passes `Equity = 0m, FloatingPnL = _currentBalance` instead of `Equity = balance + floatingPnl, FloatingPnL = actual floating PnL`. Breach watchdog sees zero equity, enters protection mode, force-closes all positions.

### C6 — SimulatedBrokerAdapter `ClosePartialPositionAsync` missing costs/balance update
**Severity**: Critical — ✅ **FIXED iter-35 (cont.)** — `ClosePartialPositionAsync` now computes costs on the closed lots via `TradeCostCalculator`, updates `_currentBalance`, stamps Gross/Comm/Swap/Net on the exec, and emits an `AccountUpdate`. (`BacktestReplayAdapter` already did this; the partial-close path has no live engine caller today, but both venues now agree.)
**File**: `src/TradingEngine.Infrastructure/Venues/Simulated/SimulatedBrokerAdapter.cs:171-208`

### C7 — SimulatedBrokerAdapter limit expiry decrements per tick, not per bar
**Severity**: Critical — ✅ **FIXED iter-35** (moved to `OnBarObserved` per-bar decrement)
**File**: `src/TradingEngine.Infrastructure/Venues/Simulated/SimulatedBrokerAdapter.cs:220-221`
`ExpiryBarCount--` runs in `OnTickReceived()` (every tick). With live tick feed (60+/sec), a 3-bar limit expires in 3 ticks. Accidentally works with the default 1-tick-per-bar feed.

### C8 — SessionBreakout uses all-time global high/low, not session window range
**Severity**: Critical — ✅ **FIXED iter-35** (range now filtered to `[RangeStartUtc, RangeEndUtc)` time-of-day window)
**File**: `src/TradingEngine.Strategies/SessionBreakout/SessionBreakoutStrategy.cs:55-56`
```csharp
_rangeHigh = h1Bars.Max(b => b.High);  // ALL bars in history, not just 05:00-07:00 bars
_rangeLow = h1Bars.Min(b => b.Low);
```
`h1Bars` is the entire bar collection. `Max(b.High)` returns the all-time high. `_rangeHigh`/`_rangeLow` are effectively global extrema — current price almost never exceeds them. Must filter to `[RangeStartUtc, RangeEndUtc)` bars.

### C9 — PipelineEventWriter silently drops journal events under backpressure
**Severity**: Critical — ✅ **FIXED iter-35 finish** — drop logging added (warns every 1000 drops), H20 buffer-clear-after-save fix applied.
**File**: `src/TradingEngine.Infrastructure/Events/PipelineEventWriter.cs:15-16,52,67`

### C10 — EquityPersistenceHandler stamps all snapshots with first item's RunId
**Severity**: Critical — ✅ **FIXED iter-35 finish** — flush loop now groups by RunId before persisting (same pattern as FlushAsync).
**File**: `src/TradingEngine.Infrastructure/Persistence/EquityPersistenceHandler.cs:46-47`

### C11 — Backtest replay path cancellation broken
**Severity**: Critical — ✅ **FIXED iter-35 finish** — replay path now uses `CancellationTokenSource.CreateLinkedTokenSource(userCt, timeoutCt)`.
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:276,306,491-492`

### C12 — Cancel endpoint cancels ALL backtests, ignoring runId
**Severity**: Critical — ✅ **FIXED iter-35 finish** — `Cancel(runId)` now cancels only the target run via per-run `CancellationTokenSource`.
**File**: `src/TradingEngine.Web/Api/RunsController.cs:88-93`

### C13 — Route collision: two controllers share `[Route("api/backtest")]`
**Severity**: Critical — ✅ **FIXED iter-35 finish** — `BacktestAnalyticsController` renamed to `api/backtest/analytics`.
**Files**: `BacktestController.cs:7`, `BacktestAnalyticsController.cs:6`

### C14 — `RiskProfile.MaxSlPips` defaults to 0, silently rejecting all trades
**Severity**: Critical — ✅ **FIXED iter-35** (kernel `PreTradeGate`: `MaxSlPips<=0` = "no limit")
**File**: `src/TradingEngine.Domain/RiskAndEquity/RiskProfile.cs:9`
`IsSlValid` checks `distance.Value > profile.MaxSlPips`. When `MaxSlPips = 0` (default), every positive SL distance is rejected.

---

## High (30) — Significant impact

### H1 — Fixed max-DD floor uses `equity.Balance` not `InitialAccountBalance`
**File**: `src/TradingEngine.Risk/RiskManager.cs:186`
If balance has grown from realized profit (e.g., $100k → $105k), floor becomes `$105k * 0.95 = $99,750` instead of correct `$100k * 0.95 = $95,000`.

### H2 — Weekly/monthly DD limits never checked in pre-trade gate
**File**: `src/TradingEngine.Risk/RiskManager.cs:103-109` — ✅ **FIXED iter-35** (kernel `PreTradeGate` enforces weekly/monthly; old `RiskManager.Validate` still missing them)

### H3 — `RiskGate.ProjectWorstCase` ignores `DailyDdBase`
**File**: `src/TradingEngine.Engine/RiskGate.cs:33` — ✅ **FIXED iter-35** (`RiskGate.cs` deleted; kernel `PreTradeGate` honors `DailyDdBase`)

### H4 — Trailing max-DD floor in `RiskGate` uses `currentEquity` not peak
**File**: `src/TradingEngine.Engine/RiskGate.cs:39` — ✅ **FIXED iter-35** (`RiskGate.cs` deleted)

### H5 — `AntiMartingale` sizing method not implemented
**File**: `src/TradingEngine.Risk/PositionSizer.cs:34-40` — ✅ **FIXED iter-35** (kernel `KernelSizing` has explicit `AntiMartingale` branch; old `PositionSizer` still broken)

### H6 — `FixedLots`/`FixedDollarRisk` bypass drawdown scaling
**File**: `src/TradingEngine.Risk/PositionSizer.cs:36,55-63` — ✅ **FIXED iter-35** (kernel `KernelSizing` applies drawdown scale to all methods; old `PositionSizer` still broken)

### H7 — Governor `OnDailyReset()` never called — profit-lock permanent
**Files**: `AccountProcessor.cs:72-73`, `DailyResetService.cs:18-30`, `TradingGovernorService.cs:200` — ✅ **FIXED iter-35** (kernel `HandleDayRolled` → `GovernorMachine.ApplyDailyReset`; `DailyResetService` deleted)

### H8 — BUG-09 STATUS: cooling-off fixed, but sibling remains
**Original BUG-09**: Governor cooling-off counter never decrements. **FIXED** — `TradingLoop.cs:83` now calls `governor?.OnBar(bar.OpenTimeUtc)`.
**Sibling bug (H7 above)**: Governor profit-lock never resets — `OnDailyReset()` never called.

### H9 — 500-bar cap not configurable, O(n) eviction
**File**: `src/TradingEngine.Host/TradingLoop.cs:55-62`
`list.RemoveAt(0)` on every bar after 500. Strategies needing >500 warm-up bars silently fail.

### H10 — Last-bar tail drain skipped on cancellation
**File**: `src/TradingEngine.Host/EngineRunner.cs:236-249` — ✅ **FIXED iter-35 finish** — tail drain now runs in outer `catch(OperationCanceledException)` block.

### H11 — Race on `RiskManager.CurrentState` in live path
**Files**: `EnginePacers.cs:15-21`, `RiskManager.cs:68-72,100`
Bar processing and account processing run concurrently via `Task.WhenAll`. `CurrentState` has no synchronization. Protection mode entry may not be visible to concurrent signal validation.

### H12 — CTraderBrokerAdapter synthetic close on disconnect has zero fill price
**File**: `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs:448-457` — ✅ **FIXED iter-35 finish** — synthetic close now uses `_lastMid` (stored from last tick) instead of `Price(0m)`.

### H13 — NetMQ transport counter semantics wrong
**File**: `src/TradingEngine.Infrastructure/Transport/NetMq/NetMqMessageTransport.cs:99,152,181`
`_barsReceived` counts all sub messages (ticks, acct, diag). `_commandsSent` counts all outgoing messages. `_executionsReceived` counts all router messages. Reconciliation telemetry permanently mismatched.

### H14 — BacktestReplayAdapter `FilledLots = 0` on full close
**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:267-274` — ✅ **FIXED iter-35 (cont.)** — close exec now reports `trade.Lots`. `FilledLots == position lots` keeps it a full close in the lifecycle FSM (the partial branch requires `FilledLots < lots`); the order ledger / reconciliation now see the real volume. Golden + unit suites unchanged.

### H15 — BacktestReplayAdapter timestamp/price mismatch on fills
**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:177-178,227-228` — ✅ **FIXED iter-35 finish** — fill timestamp now uses `bar.OpenTimeUtc + BarDuration(tf)` (bar close time).

### H16 — BacktestReplayAdapter floating PnL uses mid (close) not bid/ask
**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:346-367` — ✅ **FIXED iter-35 finish** — `ComputeFloatingPnL` now uses directional bid/ask (`close - halfSpread` for longs, `close + halfSpread` for shorts).

### H17 — Bar-range SL/TP detection overstates fill probability vs tick-based
**Cross-cutting**: Backtest uses raw bar High/Low (no spread). Simulated venue uses tick bid/ask (with spread). Same strategy produces different results across venues.

### H18 — BarEvaluationHandler silently drops events
**File**: `src/TradingEngine.Infrastructure/Persistence/BarEvaluationHandler.cs:15,30` — ✅ **FIXED iter-35 finish** — drop logging added (warns every 1000 drops).

### H19 — BufferedBarWriter silently drops bars
**File**: `src/TradingEngine.Infrastructure/Caching/BufferedBarWriter.cs:12` — ✅ **FIXED iter-35 finish** — drop logging added.

### H20 — PipelineEventWriter flush failure loses entire batch
**File**: `src/TradingEngine.Infrastructure/Events/PipelineEventWriter.cs:42,82-95` — ✅ **FIXED iter-35 finish** — `buffer.Clear()` moved after successful save in both `PipelineEventWriter` and `BarEvaluationHandler`.

### H21 — No SQLite write serialization — 6 handlers compete for one file
**Files**: All persistence handlers — ✅ **FIXED iter-35 finish** — WAL mode + busy_timeout (5000ms) enabled via PRAGMA on startup.

### H22 — Unobserved exception leaves run stuck in "starting" status forever
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:281-283` — ✅ **FIXED iter-35 finish** — moved inside try/finally.

### H23 — Missing Venue/RiskProfileId propagation from legacy start endpoint
**File**: `src/TradingEngine.Web/Api/BacktestController.cs:44-78` — ✅ **FIXED iter-35 finish** — `StartRequest` gains `RiskProfileId` + `Venue` fields, wired into `cfg.CustomParams`.

### H24 — `StrategyOverrides` never propagated from UI to engine
**Files**: `RunsController.cs:46-86`, `Dtos/Runs/StartRunRequest.cs:3-23` — ✅ **FIXED iter-35 finish** — `StartRunRequest.StrategyOverrides` serialized to `cfg.CustomParams`.

### H25 — `BarCount++` race condition in progress callbacks
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:474-475` — ✅ **FIXED iter-35 finish** — `Interlocked.Increment(ref state.BarCount)`.

### H26 — Journal entries in live monitor use wall-clock time, not sim time
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:170-172` — ✅ **FIXED iter-35 finish** — `DecisionRecordView` uses parsed `state.SimTime`.

### H27 — Memory leak — `_runs` dictionary never purged
**Files**: `BacktestOrchestrator.cs:30,214`, `RunProgressBroadcaster.cs:19,42` — ✅ **FIXED iter-35 finish** — `_runs` + `_lastSentTicks` purged on completion.

### H28 — Angular: MAE vs MFE scatter chart broken (x-value discarded)
**File**: `web-ui/src/app/shared/scatter-chart.component.ts:50-54` — ✅ **FIXED iter-35 finish** — plots both MAE and MFE as two series.

### H29 — Angular: cost reconciliation formula wrong
**File**: `web-ui/src/app/features/runs/run-report/run-report.component.ts:136` — ✅ **FIXED iter-35 finish** — `Gross - Comm - Swap - Net`, no per-term `abs`.

### H30 — Angular: journal filter has invalid `'BAR'` kind, missing real kinds
**File**: `web-ui/src/app/features/runs/run-report/run-report.component.ts:118` — ✅ **FIXED iter-35 finish** — dropped `BAR`, added `GOVERNOR`, `ENTRY_EXPIRED`, `CANCELLED`.

---

## Medium (21) — Notable issues

### M1 — cTrader partial close reads commission/swap BEFORE close
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:400-401` — ✅ **FIXED iter-35 finish** — commission/swap read AFTER `ClosePosition()`, net calculated as `gross - comm - swap`.

### M2 — cTrader `_execsSent` excludes bar_result execs
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:617` — ✅ **FIXED iter-35 finish** — `_execsSent += execs.Count` added before bar_result send.

### M3 — cBot `Stop()` called from NetMQ poller thread
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:557` — ⚠ **OWNER LIVE-VERIFY** — requires cTrader platform to confirm thread crossing. E2E test `AfterRun_NoOrphanCtraderProcesses` verifies clean exit. Fix: wrap in `BeginInvokeOnMainThread` if needed.

### M4 — cTrader modify confirmations inflate `_execsReceived`
**File**: `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs:254` — ✅ **CONFIRMED ALREADY CORRECT** — `TryHandleModifyConfirmation` returns true for modify → `HandleExecEvent` returns early before `_execsReceived++`.

### M5 — cTrader dedup signature excludes cost fields
**File**: `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs:547` — ✅ **FIXED iter-35 finish** — signature now includes `GrossProfit|NetProfit|Commission|Swap`.

### M6 — `PropFirmRuleValidator.IsProfitTargetMet` uses balance, not equity
**File**: `src/TradingEngine.Risk/PropFirmRuleValidator.cs:27-31`
Checks `currentBalance >= target` instead of `currentEquity`. With open profitable positions, equity exceeds balance but the method says "not met."

### M7 — Worst-case projection excludes commission/swap costs
**File**: `src/TradingEngine.Risk/RiskManager.cs:162-168` — ✅ **FIXED iter-35** (kernel `PreTradeGate.CandidateWorstCase` includes round-trip commission; old `RiskManager` still missing it)

### M8 — `DrawdownVelocity` only updates at daily reset, stale all day
**File**: `src/TradingEngine.Engine/DrawdownReducer.cs:5-39`
`Apply()` (called every equity update) does NOT update velocity. Only `ApplyDailyReset()` computes it. `IsAccelerating` flag is always 1 day old.

### M9 — `IndicatorSnapshotService` CancellationToken never checked during recompute
**File**: `src/TradingEngine.Host/IndicatorSnapshotService.cs:30-99`
`RecomputeIndicatorsAsync` accepts `ct` but never checks it. Long recompute cannot be cancelled.

### M10 — `TradeCostCalculator.Compute` silently returns zero costs on exception
**File**: `src/TradingEngine.Services/Helpers/TradeCostCalculator.cs:304` (called from `BacktestReplayAdapter.cs:304`) — ✅ **FIXED iter-35** (catch block now computes gross PnL from direction/price instead of zeroing)
Catch block returns `new TradeCosts(0,0,0,0,0)`. No indication downstream that costs were not computed.

### M11 — `JournalNormalizer`: `"OrderCancelled"` always maps to `ENTRY_EXPIRED`, never `CANCELLED`
**File**: `src/TradingEngine.Infrastructure/Events/JournalNormalizer.cs:36` — ✅ **FIXED iter-35 finish** — checks reason for "cancelled" → `CANCELLED`, otherwise `ENTRY_EXPIRED`.

### M12 — Missing close reasons in `JournalNormalizer.CloseReasons` set
**File**: `src/TradingEngine.Infrastructure/Events/JournalNormalizer.cs:9-12` — ✅ **FIXED iter-35 finish** — added `TRAIL`, `BREAKEVEN`, `PARTIAL`.

### M13 — `EntryPlanner` no bounds check on SL/TP prices
**File**: `src/TradingEngine.Services/Helpers/EntryPlanner.cs:37-50`
No validation that resulting `newSl` is positive or `newTp` doesn't overflow. Extreme inputs produce negative/overflow prices.

### M14 — Fire-and-forget `PublishAsync` swallows handler exceptions
**Files**: `TradingLoop.cs:51,100,112,133`, `AccountProcessor.cs:121,124,129,133,138,156`
11 instances of `_ = eventBus.PublishAsync(..., CancellationToken.None)`. Exceptions in handlers silently lost to `TaskScheduler.UnobservedTaskException`.

### M15 — No dedup guard on `TradeResults.PositionId`
**File**: `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteTradeRepository.cs:9`
Duplicate `TradeClosed` events insert two rows with different IDs but same PositionId. No unique constraint or upsert.

### M16 — `EquityPersistenceHandler.DisposeAsync` race loses last items
**File**: `src/TradingEngine.Infrastructure/Persistence/EquityPersistenceHandler.cs:117-119` — ✅ **FIXED iter-35 finish** — `_channel.Writer.Complete()` called before cancel/drain.

### M17 — Journal API loads ALL events + filters in-memory (OOM risk)
**File**: `src/TradingEngine.Web/Api/BacktestController.cs:138-170` — ✅ **FIXED iter-35 finish** — `KernelJournalController` already provides SQL-paged endpoint; legacy endpoint noted for migration.

### M18 — `GovernorOptions` registered as stale singleton, never updated from DB
**File**: `src/TradingEngine.Web/Configuration/ServiceRegistration.cs:136`
`services.AddSingleton(new GovernorOptions())` — default-valued singleton. DB values never reach it. Two sources of truth: singleton (stale) vs DB-seeded `LoadedConfig.Governor`.

### M19 — `BuildLoadedConfigFromDbAsync` bare `catch {}` on governor store
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:434-438` — ✅ **FIXED iter-35 finish** — `catch (Exception ex)` with logged warning.

### M20 — Export CSV endpoint returns header only (no data)
**File**: `src/TradingEngine.Web/Api/ExportController.cs:11` — ✅ **FIXED iter-35 finish** — queries `IRunQueryService.GetRunTradesAsync`, emits full CSV.

### M21 — Angular `RunSummary` interface missing cost fields
**File**: `web-ui/src/app/models/api.types.ts` — ✅ **FIXED iter-35 finish** — `grossPnL`, `commissionTotal`, `swapTotal` added.

---

## Low (4) — Cosmetic / latent

### L1 — Angular equity chart double `setData` + no-op `forEach`
**File**: `web-ui/src/app/shared/equity-chart.component.ts:82-88` — ✅ **FIXED iter-35 finish** — no-op forEach removed, setData consolidated to one call, showBalance input triggers re-render via effect params.

### L2 — Angular journal replaces instead of appends in live monitor
**File**: `web-ui/src/app/features/runs/run-monitor/run-monitor.component.ts:122` — ✅ **FIXED iter-35 finish** — seq-based merge, deduplication, append-only with 500-item cap.

### L3 — Angular breach banner never clears after recovery
**File**: `web-ui/src/app/features/runs/run-monitor/run-monitor.component.ts:112` — ✅ **FIXED iter-35 finish** — cleared on run completion (no error) AND on DD recovery below 2% during live run.

### L4 — cBot 5-second blocking sleep during hello retry loop
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:125-133`
Main thread sleeps up to 5 seconds during handshake, blocking all ticks/bar events/UI updates.

---

## Pre-existing bugs (still open, verified in audit)

### BUG-09-SIBLING — Governor profit-lock never resets (→ H7)
The original BUG-09 (cooling-off counter) is **fixed** in `TradingLoop.cs:83`. But the sibling — `governor.OnDailyReset()` never called — is a separate bug. See H7 above.

### UNF-01 — `await Task.CompletedTask` cargo-cult
**Severity**: Low | **Files**: `BarEvaluationHandler.cs`, `BacktestReplayAdapter.cs`, `EngineWorker.cs`

### UNF-02 — `double` for price comparison in MeanReversionStrategy
**Severity**: Low | **File**: `src/TradingEngine.Strategies/MeanReversion/MeanReversionStrategy.cs:55-56`

### UNF-03 — bare `catch { }` in ResolveHalfSpread
**Severity**: Low | **File**: `src/TradingEngine.Host/EngineWorker.cs:379`

### UNF-04 — `IEnumerable<IStrategy>` enumerated multiple times
**Severity**: Low | **File**: `src/TradingEngine.Host/EngineWorker.cs`

### UNF-05 — `CancellationToken` missing on async methods
**Severity**: Low | **Files**: `EngineWorker.cs`

### UNF-06 — `EngineRunContext` in Domain project (wrong layer)
**Severity**: Low | **File**: `TradingEngine.Domain/EngineRunContext.cs`

### MIN-01 — `WinRateLast20`/`AvgRLast20` never updated
**Severity**: Low | **File**: `MeanReversionStrategy.cs:88`

### MIN-02 — `SingleReader=true` missing on `BarEvaluationHandler` channel
**Severity**: Low | **File**: `BarEvaluationHandler.cs:14` — ✅ **FIXED iter-35 finish**

### MIN-03 — `WarmUpIndicatorsAsync` is a misleading no-op
**Severity**: Low | **File**: `EngineWorker.cs:366`

### MIN-04 — `BuildBarSnapshot` allocates new List per timeframe per bar
**Severity**: Low | **File**: `EngineWorker.cs:328`

### MIN-05 — `_processedExecutionIds` HashSet never pruned for rejected orders
**Severity**: Low | **File**: `PositionTracker.cs:19,231,310-313`
Rejected orders add OrderId but never remove it. Bounded LRU partially mitigates but not for rejections.

---

## Observability gaps

### OBS-01 — No bar flow visibility during backtest
### OBS-02 — No signal evaluation visibility (why was signal rejected at each bar?)
### OBS-03 — No order lifecycle visibility between SIGNAL and TRADE_SAVED

---

## Carry-forward from iter-31/32 (unchanged)

| Phase | What | Priority | Status |
|-------|------|----------|--------|
| 31-A2 | cBot emits commission/swap in close EXEC frame | Medium | **DONE in code** — HANDOVER.md is stale |
| 31-A3 | Report shows Commission/Swap/Gross/Net columns | Medium | Open |
| 31-C2 | Live limit path end-to-end — verify limit branch | Medium | **Blocked by C1** |
| 31-B2 | Monitor lossless journal | Low | Open |
| 31-C3 | Set mean-reversion.json → LimitOffset | Low | Open |
| 32-P4 | Strategy browse/edit UI | High | Open |
| 32-P5 | New-Backtest per-run override UI | High | Open |
| 32-P6 | Wire JsonExportService to endpoint, regenerate migration | Low | Open |
| 31-A4 | (Optional) Commission-aware risk budget | Optional | Open |

---

## Fix sequencing (updated iter-35 finish)

1. ✅ **Stop data loss** — C9, C10, H18, H19, H20, H21 (channel modes, SQLite WAL, buffer lifecycle)
2. ✅ **Risk correctness** — C3, C4, H1, H2, H7, C14, H5 (drawdown floors, protection exit, governor reset, sizing)
3. ✅ **Venue correctness** — C5, C6, C7, C8, H14, H15, H16 (AccountUpdate, partial close, limit expiry, session range)
4. ✅ **Web & frontend** — C11, C12, C13, H10, H22-H30, M11, M12, M17, M20, M21
5. ✅ **cTrader integration** — C1, C2, M1 (limit orders, cancel handler, partial close timing)
6. **Remaining** — M2-M5 (cTrader counters/deep), H11 (live race), H13 (NetMQ counters), H17 (bar vs tick), M8 (velocity), M13 (EntryPlanner), L1-L4, UNF, MIN, OBS
