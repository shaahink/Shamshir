# Shamshir — Open Issues

**Updated**: 2026-06-19 (iter-35 cont. — AF2 order-gate cutover LIVE)
**Branch**: `iter/35-kernel`
**Total open**: 60 bugs + 7 carry-forward + 3 observability gaps + 3 minor (15 resolved in iter-35)

> **iter-35 (cont.) — order-gate cutover is LIVE in production.** `TradingLoop` now decides via
> `KernelOrderGate` → `PreTradeGate` → `KernelSizing` (DI flipped; verified by the golden-harness
> equivalence test + a run-shamshir app run). So the kernel risk fixes below — **C3, H1, H2, H3, M7,
> H5, H6, C14, NEW-3** — are now **effective in production**, not just in a sidecar. `RiskManager`'s
> `ValidateOrder`/`CalculateLotSize`/`PositionSizer` order-decision path is **no longer wired** (kept
> only as the equivalence-test baseline). Remaining cutover families (bar-exit SL/TP, equity-breach
> watchdog, day/week/month resets, governor retirement, sizing-class deletion) are still pending — see
> `docs/iterations/iter-35/PLAN-FINISH-AB.md`.

Fixed items → `docs/RESOLVED-ISSUES.md`. Roadmap → `docs/NEXT-STEPS.md`.
Full audit narrative + system model → `docs/reference/SYSTEM-AUDIT.md`.
Iter-35 handover → `docs/iterations/iter-35/HANDOVER.md`.

---

## Critical (14) — Correctness-breaking, fix immediately

### C1 — cTrader limit orders always execute as market
**Severity**: Critical — all limit orders become instant market orders
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:298-345`
cBot `ExecuteSubmitOrder()` ignores `orderType`, `limitPrice`, `expiryBars`, `maxSlippagePips` from the engine. Every order is unconditionally `ExecuteMarketOrder`.

### C2 — cTrader has no `cancel_order` handler
**Severity**: Critical — limit expiry cancellations silently dropped
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:236-266`
Engine sends `{"type":"cancel_order",...}` in `bar_done.commands[]`. cBot command dispatch loop has no branch for `cancel_order`. Position state drifts between engine and cTrader.

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
**Severity**: Critical — journal audit trail has gaps
**File**: `src/TradingEngine.Infrastructure/Events/PipelineEventWriter.cs:15-16,52,67`
Channel uses `DropOldest` (50k). `TryWrite` return value discarded — zero observability of drops.

### C10 — EquityPersistenceHandler stamps all snapshots with first item's RunId
**Severity**: Critical — equity data written under wrong run
**File**: `src/TradingEngine.Infrastructure/Persistence/EquityPersistenceHandler.cs:46-47`
```csharp
var runId = buffer[0].RunId;  // first item only!
await _persistence.SaveEquitySnapshotsBatchAsync(snapshots, runId, ct);
```
When multiple runs' snapshots interleave in the channel, every snapshot in the batch gets the first run's ID. Striped data across runs.

### C11 — Backtest replay path cancellation broken
**Severity**: Critical — user Cancel button has zero effect on replay backtests
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:276,306,491-492`
`RunAsync()` receives user `CancellationToken` but `RunEngineReplayAsync()` discards it and creates an isolated 30-minute timeout `CancellationTokenSource`.

### C12 — Cancel endpoint cancels ALL backtests, ignoring runId
**Severity**: Critical — cancelling one run kills all concurrent runs
**File**: `src/TradingEngine.Web/Api/RunsController.cs:88-93`
```csharp
public async Task<IActionResult> Cancel(string runId)
{
    await _orchestrator.StopAllAsync();  // ignores runId entirely
```

### C13 — Route collision: two controllers share `[Route("api/backtest")]`
**Severity**: Critical — ambiguous route table
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
**File**: `src/TradingEngine.Host/EngineRunner.cs:236-249`
`OperationCanceledException` re-thrown inside foreach skips `AccountStream.TryRead` drain. Final PnL never reaches AccountProcessor.

### H11 — Race on `RiskManager.CurrentState` in live path
**Files**: `EnginePacers.cs:15-21`, `RiskManager.cs:68-72,100`
Bar processing and account processing run concurrently via `Task.WhenAll`. `CurrentState` has no synchronization. Protection mode entry may not be visible to concurrent signal validation.

### H12 — CTraderBrokerAdapter synthetic close on disconnect has zero fill price
**File**: `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs:448-457`
Engine injects `ExecutionEvent` with `Price(0m)` when cBot disconnects. PnL computed against zero price — corrupts trade ledger.

### H13 — NetMQ transport counter semantics wrong
**File**: `src/TradingEngine.Infrastructure/Transport/NetMq/NetMqMessageTransport.cs:99,152,181`
`_barsReceived` counts all sub messages (ticks, acct, diag). `_commandsSent` counts all outgoing messages. `_executionsReceived` counts all router messages. Reconciliation telemetry permanently mismatched.

### H14 — BacktestReplayAdapter `FilledLots = 0` on full close
**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:267-274` — ✅ **FIXED iter-35 (cont.)** — close exec now reports `trade.Lots`. `FilledLots == position lots` keeps it a full close in the lifecycle FSM (the partial branch requires `FilledLots < lots`); the order ledger / reconciliation now see the real volume. Golden + unit suites unchanged.

### H15 — BacktestReplayAdapter timestamp/price mismatch on fills
**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:177-178,227-228`
Fill timestamp = `bar.OpenTimeUtc` but fill price = `bar.Close`. Affects `CountNightsHeld` swap calculation for boundary-crossing trades.

### H16 — BacktestReplayAdapter floating PnL uses mid (close) not bid/ask
**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:346-367`
Unrealized PnL computed with `close` price instead of directional bid/ask. Overstates by ~half spread per position. Breach watchdog reads inflated equity.

### H17 — Bar-range SL/TP detection overstates fill probability vs tick-based
**Cross-cutting**: Backtest uses raw bar High/Low (no spread). Simulated venue uses tick bid/ask (with spread). Same strategy produces different results across venues.

### H18 — BarEvaluationHandler silently drops events
**File**: `src/TradingEngine.Infrastructure/Persistence/BarEvaluationHandler.cs:15,30`
`DropOldest` + `TryWrite` return ignored. Bar evaluation analytics data can vanish silently.

### H19 — BufferedBarWriter silently drops bars
**File**: `src/TradingEngine.Infrastructure/Caching/BufferedBarWriter.cs:12`
`DropOldest` on bar persistence channel. Drops mean missing OHLCV data in SQLite.

### H20 — PipelineEventWriter flush failure loses entire batch
**File**: `src/TradingEngine.Infrastructure/Events/PipelineEventWriter.cs:42,82-95`
`buffer.Clear()` at TOP of loop iteration. If `SaveChangesAsync` throws, the next loop iteration clears the buffer before any retry — events permanently lost. Same pattern in `BarEvaluationHandler`.

### H21 — No SQLite write serialization — 6 handlers compete for one file
**Files**: All persistence handlers
Six independent background writers compete for the same SQLite file. No WAL mode configured. No retry with exponential backoff. Sporadic "database is locked" silently swallowed in catch blocks.

### H22 — Unobserved exception leaves run stuck in "starting" status forever
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:281-283`
`ResolveEffectiveConfigJsonAsync()` and `WriteStartRecordAsync()` called BEFORE try/catch block. If either throws, `state.Status` stays `"starting"`, finally block never runs, `_progressStore` never completed.

### H23 — Missing Venue/RiskProfileId propagation from legacy start endpoint
**File**: `src/TradingEngine.Web/Api/BacktestController.cs:44-78`
Legacy `POST /api/backtest/start` doesn't send `Venue` or `RiskProfileId`. Runs from this endpoint always use DB defaults.

### H24 — `StrategyOverrides` never propagated from UI to engine
**Files**: `RunsController.cs:46-86`, `Dtos/Runs/StartRunRequest.cs:3-23`
`StartRunRequest` has no `StrategyOverrides` field. `ParseOverrides()` always returns empty dictionary. Per-run parameter tweaks from UI cannot reach the engine.

### H25 — `BarCount++` race condition in progress callbacks
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:474-475`
Non-atomic increment from concurrent `Progress<T>` callbacks. Progress bar under-reports on high-frequency backtests.

### H26 — Journal entries in live monitor use wall-clock time, not sim time
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:170-172`
`DecisionRecordView` uses `DateTime.UtcNow` instead of simulation time.

### H27 — Memory leak — `_runs` dictionary never purged
**Files**: `BacktestOrchestrator.cs:30,214`, `RunProgressBroadcaster.cs:19,42`
Both `_runs` `ConcurrentDictionary` and `_lastSentTicks` dictionary accumulate per-run entries indefinitely. Long-running web server exhausts memory.

### H28 — Angular: MAE vs MFE scatter chart broken (x-value discarded)
**File**: `web-ui/src/app/shared/scatter-chart.component.ts:50-54`
Maps only `d.y` (MFE), `d.x` (MAE) completely discarded. Chart shows "MFE vs Index" instead of "MAE vs MFE".

### H29 — Angular: cost reconciliation formula wrong
**File**: `web-ui/src/app/features/runs/run-report/run-report.component.ts:136`
Uses `abs(Gross) - abs(Comm) - abs(Swap) - Net` instead of `Gross - Comm - Swap - Net`. Shows false "MISMATCH" badges even when costs are correct.

### H30 — Angular: journal filter has invalid `'BAR'` kind, missing real kinds
**File**: `web-ui/src/app/features/runs/run-report/run-report.component.ts:118`
Frontend includes `'BAR'` (no such backend kind — filter returns zero results). Missing filter buttons for `GOVERNOR`, `ENTRY_EXPIRED`, `CANCELLED`.

---

## Medium (21) — Notable issues

### M1 — cTrader partial close reads commission/swap BEFORE close
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:400-401`
Partial close reads `pos.Commissions * fraction` BEFORE `ClosePosition()` executes. Full close correctly reads after. Partial close commission may be understated by ~50%.

### M2 — cTrader `_execsSent` excludes bar_result execs
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:617`
Only incremented for standalone venue-initiated exec frames. Bar_result execs not counted. Reconciliation counter permanently mismatched.

### M3 — cBot `Stop()` called from NetMQ poller thread
**File**: `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs:557`
cAlgo `Robot.Stop()` should be called from main robot thread, not the NetMQ poller background thread.

### M4 — cTrader modify confirmations inflate `_execsReceived`
**File**: `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs:254`
`count++` includes modify confirmations which are handled separately. Inflates the counter relative to actual fills.

### M5 — cTrader dedup signature excludes cost fields
**File**: `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs:547`
Signature: `$"{exec.OrderId}|{exec.NewState}|{exec.FillPrice}|{exec.FilledLots}"` — excludes GrossProfit/NetProfit/Commission/Swap. Cost corrections silently dropped as duplicates.

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
**File**: `src/TradingEngine.Services/Helpers/JournalNormalizer.cs:36`
All cancellations map to `ENTRY_EXPIRED`, even non-expiry cancellations (manual, broker rejection). The `InferFromReason` fallback checks for "cancelled" but is never reached because the switch matches first.

### M12 — Missing close reasons in `JournalNormalizer.CloseReasons` set
**File**: `src/TradingEngine.Services/Helpers/JournalNormalizer.cs:9-12`
Missing: `"TRAIL"`, `"BREAKEVEN"`, `"PARTIAL"`. Closes with these reasons won't normalize to `CLOSE`.

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
**File**: `src/TradingEngine.Infrastructure/Persistence/EquityPersistenceHandler.cs:117-119`
`FlushRemainingAsync()` drains channel, then `_channel.Writer.Complete()` is called. Items written between these two calls are readable but `_cts.Cancel()` kills the flush loop before they're flushed.

### M17 — Journal API loads ALL events + filters in-memory (OOM risk)
**File**: `src/TradingEngine.Web/Api/BacktestController.cs:138-170`
`GetByRunIdAsync()` fetches entire event set, then `.AsEnumerable()` filters in LINQ-to-objects. For runs with 100k+ events, every poll loads all into memory.

### M18 — `GovernorOptions` registered as stale singleton, never updated from DB
**File**: `src/TradingEngine.Web/Configuration/ServiceRegistration.cs:136`
`services.AddSingleton(new GovernorOptions())` — default-valued singleton. DB values never reach it. Two sources of truth: singleton (stale) vs DB-seeded `LoadedConfig.Governor`.

### M19 — `BuildLoadedConfigFromDbAsync` bare `catch {}` on governor store
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:434-438`
```csharp
catch { governor = baseConfig.Governor; }
```
DB error silently falls back to JSON defaults. No log, no alert.

### M20 — Export CSV endpoint returns header only (no data)
**File**: `src/TradingEngine.Web/Api/ExportController.cs:11`
```csharp
var csv = "Symbol,Direction,Lots,EntryPrice,ExitPrice,NetPnL,ExitReason,Date\n";
return Content(csv, ...);
```
No database query, no trade rows. Dead endpoint.

### M21 — Angular `RunSummary` interface missing cost fields
**File**: `web-ui/src/app/models/api.types.ts`
`RunListResponse` has `GrossPnL`, `CommissionTotal`, `SwapTotal` but the Angular `RunSummary` interface omits them. Cost data invisible on run list page.

---

## Low (4) — Cosmetic / latent

### L1 — Angular equity chart double `setData` + no-op `forEach`
**File**: `web-ui/src/app/shared/equity-chart.component.ts:82-88`
No-op `forEach` mutates objects pointlessly. `setData()` called twice. `showBalance` input change doesn't trigger re-render.

### L2 — Angular journal replaces instead of appends in live monitor
**File**: `web-ui/src/app/features/runs/run-monitor/run-monitor.component.ts:122`
`journalEntries.set(mapped.slice(-200))` replaces entire array. If a progress message has fewer entries than before, entries disappear.

### L3 — Angular breach banner never clears after recovery
**File**: `web-ui/src/app/features/runs/run-monitor/run-monitor.component.ts:112`
Once `breachBanner` set, never cleared back to `null`.

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
**Severity**: Low | **File**: `BarEvaluationHandler.cs:14`

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

## Fix sequencing

1. **Stop data loss** — C9, C10, H18, H19, H20, H21 (channel modes, SQLite WAL, buffer lifecycle)
2. **Risk correctness** — C3, C4, H1, H2, H7, C14, H5 (drawdown floors, protection exit, governor reset, sizing)
3. **Venue correctness** — C5, C6, C7, C8, H14, H15, H16 (AccountUpdate, partial close, limit expiry, session range)
4. **Web & frontend** — C11, C12, C13, H22, H23, H24, H25, H27, H28, H29, H30
5. **cTrader integration** — C1, C2, M1 (limit orders, cancel handler, partial close timing)
