# Iter-24 Handover — 2026-06-15 (final)

**Branch**: `iter/24-build` (9 commits on top of `iter/23-close-gap`).
**Read first**: `SYSTEM-MODEL.md` (engine model + findings), `PLAN.md` (phases + decisions).

## Working rules
- Failing-test-first. Build + fast suites green at **every** commit.
- Fast suites (trust these; <2s, no IHost, no orphans):
  `dotnet test tests/TradingEngine.Tests.Unit` (166), `dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName~Ftmo"` (10).
  Current baseline: **166 / 10 green**.
- **Use `EngineHarnessBuilder`** for deterministic FTMO/constraint tests — direct `TradingLoop`, real `RiskManager`, `<1s`.
  Do NOT use `ReplayTestHarness`/`CtraderTestHarness` (IHost ~60s floor, mocks RiskManager, async stop unreliable).
- Full-solution build fails on `aspire/AppHost` (NU1903 MessagePack) — build/test the test projects directly, don't "fix" it.
- Kill stray `ctrader-cli` before cTrader tests; `CtraderProcessGuard` + `CtraderTestFixture` handles this automatically.

## What's DONE this iteration

### Test infrastructure (Phase 0)
- **0a — `EngineHarnessBuilder`** (deterministic, IHost-free): composes real `RiskManager` + `WireRiskRules`,
  `FakeVenue` (minimal `IBrokerAdapter`), `PositionTracker`, `OrderDispatcher`, direct `TradingLoop`.
  `DriveBarsAsync` runs synchronously per bar: equity-pump → process → drain fills → SL/TP exit → drain.
  `BacktestActuallyTradesTests` un-skipped, green in <500ms.
- **0b — orphan process guard**: `CtraderProcessGuard.KillStrays()` + `CtraderTestFixture` (xUnit collection fixture)
  that kills pre-existing `ctrader-cli` before and asserts zero orphans after. Registered on `CtraderSerialCollection`.
- **0c — `TradingLoop`** extracted: already DONE. `TradingLoopDirectTests` (1 green, ~1s). The harness uses this directly.
- **0d — `EngineRunner`** split from `EngineWorker`: already DONE (17 fields, no hosting dependency).
- **0e — concurrency stress test**: `PositionTrackerConcurrencyTests` — 50 concurrent fills + force-close
  racing `TrackOrder`, verifying `SemaphoreSlim(1,1)` guard. 2 tests green.
- **0f — venue-decoupled**: `EngineRunner` no longer type-sniffs adapters: already DONE.
  **IEnginePacer deferred** — removing `if (_engineMode == Backtest)` fork is the remaining background task.

### Core fixes (Phases 1–4)
- **Phase 1 — breach watchdog**: `CheckBreachAsync` in `EngineHarness` mirrors `AccountProcessor`'s breach logic.
  Configurable `FlattenAtFraction` + `WithoutBreachWatchdog()`. 3 FTMO halt tests (daily-DD breach, max-loss,
  protection-mode blocks orders) green in <700ms.
- **Phase 2 — kernel quarantine**: UNWIRED banners on 5 dead reducer branches (`HandleEquityObserved`,
  `HandleTickReceived`, `HandleBarClosed`'s governor/SL-TP, `HandleDayRolled`, `HandleWeekRolled`).
  Architecture test (`EngineReducerWiringTests`) pins 6 wired vs 16 unwired `EngineEvent` types.
- **Phase 3 — `ConstraintSet`**: single resolved record projected from `RiskProfile` + `PropFirmRuleSet` at startup
  in `WireRiskRules`. All DD/loss limits normalized to `decimal`. Fixed ValidateOrder worst-case daily floor
  (was `1 - 5.0 = -4` (never blocked), now `1 - 0.05 = 0.95`. Fixed `RiskPerTradePercent` normalization.
  RiskManager `Validate`, `ValidateOrder`, `ValidateBudgetEntry` all consume `Constraints`.
- **Phase 4 — real open positions**: `TradingLoop.MapOpenPositionsToProjected()` maps `PositionTracker.OpenPositions`
  → `IReadOnlyList<ProjectedPosition>`. `PortfolioWorstCaseTests` proves N+1th order blocked by combined worst-case.

### FTMO golden journeys (Phase 5)
- 4 deterministic tests on `DrawdownState` / `Risk.CurrentState` / `FakeVenue`:
  max-loss accumulation, profit-target (TP on up-leg), lot sizing vs risk-%, exposure cap via portfolio projection.
  All green, <600ms total.

### Venue / account / PnL integrity (Phase 6 — partial)
- **M1** (venue-authoritative PnL): already DONE. Asserting test deferred (needs live/fake-transport harness).
- **M2** (synthetic close): ✅ — `CTraderBrokerAdapter` disconnected-close fill now writes `Price(0)` with
  `GrossProfit/NetProfit/Commission/Swap = 0`, preventing garbage PnL from entering the trade ledger.
- **A1** (reset timestamp): ✅ — daily/weekly/monthly reset boundaries keyed on `update.TimestampUtc`
  (venue-authoritative), not `_clock.UtcNow`.
- **A3** (Balance==0 guard): ✅ — `AccountProcessor.HandleAsync` skips `InitializeDrawdownIfNeeded` when
  `Balance == 0`, preventing zero-drawdown-base that blocks all trades.
- **V1** (startup/reconnect reconciliation): deferred (needs cTrader CLI integration).
- **V2** (durable Guid↔venue-id map): deferred.
- **V5** (buffered commands on disconnect): deferred.
- **V3** (venue-confirmed SL writeback): deferred.
- **A2, A4** (daily DD base, FloatingPnL): deferred.

### Bonus bug fixes discovered during implementation
- **OrderSubmitted didn't carry SL/TP**: `EngineReducer.HandleOrderSubmitted` hardcoded `new Price(0)` for SL
  and `null` for TP, making backtest `SimulateBarExits` inoperative. Added optional `StopLoss`/`TakeProfit` params
  to `OrderSubmitted` record, updated `PositionTracker.TrackOrder` and `EngineReducer.HandleOrderSubmitted`.
  Removed dead `CreateIntended` call in `TrackOrder` that was overwritten by the reducer.
- **Dead `CreateIntended` in `TrackOrder`**: was creating a `PositionState` with correct SL/TP, then discarding it
  and passing `OrderSubmitted` without SL/TP to the reducer. Removed the dead code.

## Your queue (highest value first)

### Phase 0f residual — IEnginePacer
Remove the last `if (_engineMode == Backtest)` fork in `EngineRunner` via an `IEnginePacer` or venue-drive
abstraction. Two impls: async-stream (Live/Paper) and bar-stepped+synchronous-fills (Backtest). Mode picks
the pacer at composition. The live `Task.WhenAll` path must stay byte-for-byte identical.
Also de-sniff `DataFeedService` (3× `SimulatedBrokerAdapter`).

### Phase 6 remaining — venue/account/PnL integrity (SYSTEM-MODEL §3.6)
Ordered by money/data risk:
1. **V1 — startup/reconnect position reconciliation**: `GetAccountStateAsync` returns `(0,0,[])` today.
   Seed `PositionTracker` from venue-open positions on connect; resync on reconnect.
   Gate: engine restarted with an open venue position can force-close it.
2. **V2 — durable Guid↔venue-position-id mapping** that survives reconnect (engine or cBot side).
3. **V5 — don't drop `_bufferedCommands` on mid-bar disconnect**: re-queue them like `_pendingCommands`.
4. **M1 asserting test**: a close exec with commission/swap → `TradeResult.NetPnL ≠ GrossPnL` (needs live transport harness).
5. **V3 — write venue-confirmed SL back to `PositionState.CurrentStopLoss`** after a modify (trailing).
6. **A2 — honor `DailyDdBase` in `OnDailyReset`**: daily-reset baseline always `update.Equity` regardless of
   `DailyDdBase`; positions spanning midnight bake floating PnL into the new daily-start.
7. **A4 — unify `FloatingPnL` definition**: `equity−balance` vs explicit `floatingPnL` field.

### Other backlog (carry-forward)
- Strategy-bank / regime tuning, Blazor UI, Scrutor assembly scanning, retiring `TradingGovernorService`
  (tracked in iter-23 handover deferred list).
- `MonthRolled` path / remove `ApplyMonthlyReset` dead method (Phase 4 unfinished item).
- `EffectExecutor` / full persistence chain in EngineHarness for DB-backed assertions (currently in-memory only).

## Commit log (iter-24 build — `iter/24-build`)

```
97140bb feat(iter24-p5-p6-p0e): golden journeys + venue fixes + concurrency stress test
c8e553d fix(iter24-p6): M2 synthetic close PnL, A3 Balance==0 guard, A1 reset timestamp
10f0efa feat(iter24-p5): FTMO golden journeys + constraint matrix tests
4892f42 feat(iter24-p4): pass real open positions into DispatchAsync + portfolio test
0a90685 feat(iter24-p3): ConstraintSet resolved record — one limit config, decimal money math
0fc309e feat(iter24-p2): quarantine dead kernel branches + architecture test
97fee25 feat(iter24-p0b): orphan process guard — CtraderProcessGuard + collection fixture
ada790a feat(iter24-p1): FTMO breach watchdog — daily DD halt + force-close
89e6b4c feat(iter24-p0a): deterministic FTMO harness + fix OrderSubmitted SL/TP
```

## Key files added/modified

### New files (test harness)
- `tests/.../Harness/EngineHarnessBuilder.cs` — `EngineHarness` + `EngineHarnessBuilder` (deterministic FTMO harness)
- `tests/.../Harness/FakeVenue.cs` — minimal `IBrokerAdapter` for synchronous testing
- `tests/.../Harness/InMemoryBarRepository.cs` — wraps `List<Bar>` as `IBarRepository`
- `tests/.../Harness/CtraderProcessGuard.cs` — static `KillStrays()` / `StrayCount()`
- `tests/.../Harness/AlwaysSignalStrategy.cs` — fires once per test run
- `tests/.../Harness/RepeatingSignalStrategy.cs` — fires ~2/3 of bars (accumulates losses)
- `tests/.../Harness/RapidFireStrategy.cs` — fires every bar (portfolio overlap tests)

### New files (tests)
- `tests/.../Ftmo/BacktestActuallyTradesTests.cs` — un-skipped, green: orders + closes + drawdown
- `tests/.../Ftmo/BacktestTradesAndHaltsTests.cs` — 3 tests: drawdown accumulates, daily-DD breach, protection-mode blocks
- `tests/.../Ftmo/PortfolioWorstCaseTests.cs` — portfolio worst-case blocks N+1th
- `tests/.../Ftmo/FtmoGoldenJourneyTests.cs` — 4 tests: max-loss, profit target, lot sizing, exposure cap
- `tests/.../Unit/Architecture/EngineReducerWiringTests.cs` — pins wired vs unwired EngineEvent types
- `tests/.../Unit/Concurrency/PositionTrackerConcurrencyTests.cs` — 2 concurrency stress tests

### New files (production)
- `src/TradingEngine.Domain/RiskAndEquity/ConstraintSet.cs` — resolved limit config, decimal money math

### Modified files (production)
- `src/TradingEngine.Domain/Events/EngineEvent.cs` — `OrderSubmitted` + optional `StopLoss`/`TakeProfit`
- `src/TradingEngine.Engine/EngineReducer.cs` — `HandleOrderSubmitted` passes SL/TP, UNWIRED banners
- `src/TradingEngine.Services/PositionTracker.cs` — `TrackOrder` passes SL/TP; removed dead `CreateIntended`
- `src/TradingEngine.Host/TradingLoop.cs` — `MapOpenPositionsToProjected()`, passes real open positions
- `src/TradingEngine.Risk/RiskManager.cs` — `Constraints` property, `SetConstraints`, all consumers updated
- `src/TradingEngine.Host/AccountProcessor.cs` — A1 (reset timestamp), A3 (Balance==0 guard), Constraints
- `src/TradingEngine.Host/EngineHostFactory.cs` — `WireRiskRules` projects ConstraintSet
- `src/TradingEngine.Host/EngineServiceCollectionExtensions.cs` — extension-method WireRiskRules updated
- `src/TradingEngine.Domain/Interfaces/IRiskManager.cs` — `Constraints` on interface
- `src/TradingEngine.Domain/RiskAndEquity/EngineState.cs` — UNWIRED XML doc
- `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs` — M2: synthetic close PnL=0

## Test suite state (2026-06-15)

| Suite | Passed | Failed | Skipped | Notes |
|-------|--------|--------|---------|-------|
| Unit (166) | 166 | 0 | 0 | All green, <2s |
| FTMO harness (10) | 10 | 0 | 0 | All deterministic, <1s total |
| Simulation (37) | 26 | 11 | 0 | 11 pre-existing: cTrader CLI / ReplayTestHarness / NetMQ |
