# Iter-24 Plan — One Strong Core, Provable Against FTMO

Context: see `SYSTEM-MODEL.md`. The engine has a clean pure kernel but only the
position-lifecycle slice is wired; the backtest path is a stale fork that places **0
trades** and enforces **0 constraints**. This iteration collapses the two trading loops
into one, makes one source of truth per concept, and proves FTMO constraints with
deterministic tests over a backtest that actually trades.

Working style: failing-test-first; build + full test suite green at **every** commit;
small phases with machine-checkable gates. Branch off `iter/23-close-gap`.

Baseline note: the full-solution build fails on an unrelated `aspire/AppHost` NuGet
vuln-as-error (`NU1903 MessagePack`). Build/test the test projects directly, e.g.
`dotnet test tests/TradingEngine.Tests.Simulation` — do not "fix" AppHost here.

---

## Phase 1 — One trading loop (the core fix)

**Goal:** a single `TradingLoop` used by both live and backtest; delete `BacktestDriver`;
backtest actually trades and enforces constraints.

1. **RED:** Add `tests/.../Simulation/Ftmo/BacktestTradesAndHaltsTests.cs`:
   - Fixture: FTMO-standard ruleset, `AlwaysSignalStrategy` (or a deterministic SL/TP
     strategy), ~200 H1 bars on EURUSD with a monotonic down-leg.
   - Assert (currently fails): `db.Trades.Count > 0`; at least one trade has a real
     entry+exit; once daily DD crosses `MaxDailyLossPercent·FlattenAtFraction`, a
     `RequestForceCloseAll` fires and no new orders open afterward that day.
2. Extract `TradingLoop` (new file in `TradingEngine.Host`) containing the per-bar body
   currently in `EngineWorker.ProcessBarsAsync:204-335`: indicator recompute → bar
   snapshot → regime → active strategies → evaluate → signal gate → dispatch →
   `TrackOrder` → drain executions. It takes the collaborators via a small deps struct
   (reuse `IndicatorSnapshotService`, `OrderDispatcher`, `PositionTracker`, `SignalGate`,
   `StrategyBank`, `RegimeDetector`, `EventBus`, journal, progress, clock).
3. Live path: `EngineWorker` calls `TradingLoop.ProcessBarAsync(bar)` from its
   `BarStream` loop. Behavior-preserving; suite stays green.
4. Backtest path: `RunBacktestLoopAsync` drives the **same** `TradingLoop`, plus:
   - feed **every** account update through `AccountProcessor.HandleAsync` (not just init)
     so equity, the breach watchdog, and daily/weekly/monthly resets run in backtest;
   - move SL/TP fill simulation into the replay/simulated adapter (a live broker does it
     server-side), or into a single shared `SimulatedExitChecker` the adapter owns.
5. **Delete `BacktestDriver.cs`.** Migrate `ReplayTestHarness` + `CtraderTestHarness` to
   the unified loop.
6. Fix the vacuous assertion in `BacktestReplayTests` (it loops over `trades` which is
   empty when 0 trades) to assert `trades.Count > 0`.

**Gates:** `grep -rc "class BacktestDriver" src` = 0; new FTMO test green; full suite
green; backtest run produces `Trades.Count > 0`.

---

## Phase 2 — One source of truth for drawdown & governor

**Decision to make first (record it in the commit):** owner = `RiskManager`
(pragmatic, already wired) **or** finish-wire the kernel. Recommended: **RiskManager owns
runtime risk state; delete the dead kernel branches.**

- Delete dead reducer code that nothing feeds: `HandleEquityObserved`,
  `HandleTickReceived`, `HandleBarClosed`'s `GovernorMachine.ApplyBar` + `DetectSlTpExit`,
  `HandleDayRolled`/`HandleWeekRolled`, and the unused `EngineState.Drawdown`/`Governor`
  fields **OR** wire them and delete the imperative copies — not both.
- Collapse the 4 drawdown representations: `EquitySnapshot` carries the numbers;
  `ExtendedRiskState` is the only mirror; remove duplication.

**Gates:** no `EngineEvent` type is constructed-but-never-reduced (add an architecture
test); single class owns `DrawdownState`.

---

## Phase 3 — One limit config

- Collapse `RiskProfile` (DD percents, exposure, max positions) and `PropFirmRuleSet`
  (loss percents, daily base) into one resolved `ConstraintSet` consumed identically by
  the pre-trade gate, the worst-case projection, and the watchdog.
- Normalize `MaxDailyDrawdownPercent`/`MaxTotalDrawdownPercent` to `decimal` (kill the
  `(decimal)double` casts on money math).

**Gates:** one type holds each limit; `grep` finds no `(decimal)profile.Max*DrawdownPercent`.

---

## Phase 4 — Correctness fixes surfaced while modeling

- Pass real open positions (not `[]`) into `OrderDispatcher.DispatchAsync` →
  `RiskManager.ValidateOrder` worst-case projection sees portfolio risk.
- Add `MonthRolled` path or remove `ApplyMonthlyReset` dead method.

**Gates:** a portfolio test where N open positions' combined worst-case blocks the N+1th.

---

## Phase 5 — Prove FTMO with deterministic golden journeys

- One canonical FTMO journey fixture: equity walks into (a) the daily-loss line → halt +
  flatten + next-day reset re-enables; (b) the max-loss line → permanent halt; (c) the
  profit target. Lock as a golden over `DrawdownState` + decision journal.
- Constraint matrix tests: lot sizing == risk-%, daily/total DD halt thresholds,
  exposure cap, max-concurrent, weekend/news — each a focused deterministic test
  asserting on state/journal, not log strings.

**Gates:** golden journey stable across runs; each constraint has ≥1 asserting test.

---

## Out of scope (carry-forward)
- Strategy-bank / regime tuning, Blazor UI, Scrutor assembly scanning, retiring
  `TradingGovernorService` (tracked in iter-23 handover deferred list).
