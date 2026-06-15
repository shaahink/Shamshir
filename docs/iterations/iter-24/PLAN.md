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

## Phase 0 — Test infrastructure (do this FIRST; it unblocks proving everything else)

The current tests can't actually validate money/risk behaviour, and they leak processes.
Two design problems and one seam:

### 0a. Deterministic FTMO harness — `EngineHarnessBuilder` (skeleton landed, compiles)
`ReplayTestHarness` is unusable as an assertion vehicle: it **mocks `IRiskManager`** (so it
can't assert real drawdown/limits) and it blocks on `BarStream.Completion` + a hardcoded
`Task.Delay(5_000)` + host shutdown (~60s floor that times out any real-work test).

A skeleton `tests/.../Harness/EngineHarnessBuilder.cs` + `EngineHarness` is in place. The
reusable **`WaitForQuiescenceAsync`** (stop on "all fed bars consumed and settled", no
wall-clock delay) is implemented. **AGENT TODO:**
- Implement `BuildAsync`: compose via the real `AddRisk` / `AddPersistence` / `AddStrategies`
  / `AddEventInfrastructure` / `AddEngineWorker` extensions, swap in a deterministic in-memory
  broker (real in-memory `IBarRepository` → `BacktestReplayAdapter` is fine), call
  `WireRiskRules` so the prop-firm rule set is active, subscribe the persistence handlers.
- Expose `barsConsumed`/`barsFed` from the fake broker so `WaitForQuiescenceAsync` works.
- Move `BacktestActuallyTradesTests` onto this harness and **un-skip it** (RED→GREEN).

**Gate:** `BacktestActuallyTradesTests` green in <10s with a real `RiskManager`.

### 0b. Process-leak fix — `ChildProcessReaper` (landed) + sweep (TODO)
Root cause of the recurring orphan: `CTraderCli` launched `ctrader-cli` via CliWrap; on
cancel/crash only the direct child was killed, so the CLI's own children survived (one was
found alive for **2 days**). Fixed with `src/TradingEngine.CTraderRunner/ChildProcessReaper.cs`
— a Windows Job Object (`KILL_ON_JOB_CLOSE`) the current process joins, so every descendant
dies when the launcher exits. `CTraderCli.BacktestAsync` now arms it before spawning.
**AGENT TODO:** verify `CtraderTestHarness` exits leave no `ctrader-cli` (assert via a
collection-fixture teardown that `Get-Process ctrader-cli` is empty), and add a defensive
sweep helper for CI.

**Gate:** running the full cTrader test suite twice leaves zero `ctrader-cli` processes.

### 0c. Testability seam — extract `TradingLoop` (DONE)
`src/TradingEngine.Host/TradingLoop.cs` is the per-bar body as a standalone unit taking
explicit collaborators; `EngineWorker` constructs one and delegates from both the live and
backtest loops (`_tradingLoop.ProcessBarAsync(bar, ct)`). `BarCount`/`Reset` moved onto it.
Behaviour-preserving (Unit 163, Goldens 7 green). It can now be driven directly with a fake
broker + the real risk pipeline, no IHost.

Done: `TradingLoopDirectTests.TradingLoop_DrivenDirectly_ProducesAnOrder` constructs
`TradingLoop` with a fake broker + real `OrderDispatcher`/`PositionTracker`/
`IndicatorSnapshotService` (NSubstitute for the rest), drives 8 bars, asserts an order is
submitted — green in ~1s, no IHost. **This is the template the FTMO suite reuses.**

**AGENT TODO:**
- Point `EngineHarnessBuilder` at `TradingLoop` directly for the deterministic FTMO tests:
  reuse the `TradingLoopDirectTests` wiring but swap the substitute `IRiskManager` for a
  **real `RiskManager`** (with an active prop-firm rule set via `WireRiskRules`) and a real
  persistence path, drive bars synchronously, and assert on `RiskManager.Drawdown` / the
  journal. This sidesteps the IHost entirely — the preferred path over booting `EngineWorker`.

**Gate:** ✅ the direct `TradingLoop` unit test is green and runs in <1s.

### 0d. De-god the worker (DONE)
`EngineWorker` was a 300-line `BackgroundService` holding ~28 injected fields (five dead:
`_riskProfileResolver`, `_crossRateProvider`, `_persistence`, `_governor`, `_loggerFactory`).
Split into `EngineRunner` (plain class, all run logic, `RunAsync(ct)`, 17 fields, no hosting
dependency) and a ~16-line `EngineWorker : BackgroundService` that just delegates. Dead fields
and the unused `ILoggerFactory` ctor dependency removed (3 construction sites updated).
Behaviour-preserving (Unit 163, Golden+Loop 8 green).

### 0e. Live-path concurrency + lean tick hot path (DONE)
Fixed the `PositionTracker` race documented in `SYSTEM-MODEL.md §3.4`:
- `ProcessTicksAsync` is now a lean hot path — translate ticks only, never touch `PositionTracker`.
- One serialized live execution consumer (`MarketEventSource.ConsumeExecutionsAsync`, single reader)
  pairs with the single-writer `ProcessExecutionEventsAsync`; `TradingLoop` no longer drains
  executions (backtest caller drains explicitly; `marketEvents`/`strategies` dropped from its ctor).
- `PositionTracker` serializes its three mutators with a `SemaphoreSlim(1,1)`.
Behaviour-preserving (Unit 163, Golden+Loop 8 green).

**AGENT TODO:** add a live-path concurrency stress test (many fills + force-close racing TrackOrder)
to lock the invariant in; consider draining remaining executions on shutdown.

### 0f. Decouple the engine from concrete venues (DONE; deeper part queued)
`EngineRunner` no longer type-sniffs `CTraderBrokerAdapter` / `SimulatedBrokerAdapter` /
`BacktestReplayAdapter`. The four sniffed behaviours are now default-no-op `IBrokerAdapter` methods
(`RegisterConnectedHandler`, `OnTickObserved`, `OnBarObserved`, `CompleteBarAsync(ct)`) overridden by
the venue that needs them (see `SYSTEM-MODEL §3.4b`). Behaviour-preserving (Unit 163, Golden+Loop 8).

**AGENT TODO (architecture):** remove the last backtest branch — `if (_engineMode == Backtest)` still
forks `EngineRunner` into two top-level loops. Introduce an `IEnginePacer` (or venue-drive) abstraction
that owns pacing (bar-stepped+synchronous-fills vs async streams) so the engine runs ONE path. Then
`EngineMode` leaves the run logic entirely (it only stamps `EquitySnapshot.Mode` via `AccountProcessor`).
Also de-sniff `DataFeedService` (3× `SimulatedBrokerAdapter`). Related core review the user flagged —
queue as separate findings: **journaling** (`IPipelineJournal` + `IDecisionJournal` both map to
`PipelineEventWriter` — confirm one writer, no double-write; ensure `RecordDecisionEvent` effects and
`OrderDispatcher`'s journal entries don't duplicate), **account sizing** (`RiskManager.CalculateLotSize`
+ `SizeModifierPipeline` — verify one sizing path, decimal money math per §3.5), **closing** (SL/TP exit
reason determined in 3 places per §3.3 — unify), and **venue state syncing** (reconnect → `ResetState`
now via `RegisterConnectedHandler`; verify startup reconciliation + mid-session reconnect replays open
positions correctly).

---

## Phase 1 — One trading loop (the core fix)
**(P1 loop unification + delete BacktestDriver is DONE in commit iter24-p1; remaining items below)**

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
