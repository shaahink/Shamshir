# Shamshir — System Model (as-built, 2026-06-15)

Purpose: model what this engine **actually is today**, how the logic is glued across
multiple symbols, timeframes, indicators, and positions, and exactly where it stays
true to constraints vs. where it silently does not. Written before the iter-24 unify
work so we agree on the territory.

> **Status update (2026-06-18):** This model was written pre-iter-24. The iter-31/32
> combined work (costs, journal, order entry, config store) landed on the credential-free
> backtest path. See sections below for what changed. Key additions not reflected in this
> model: `TradeCostCalculator`, `EntryPlanner`, `JournalNormalizer`, `EffectiveConfigResolver`,
> `IStrategyConfigStore`. The backtest path (BacktestReplayAdapter) now actually trades
> with honest costs. The kernel (EngineState/EngineReducer) remains half-wired as described
> in §3.2 — the imperative path is still the active path for drawdown/governor.

> TL;DR — There is a clean pure **kernel** (`EngineState` + `EngineReducer` +
> `PositionLifecycle` + `DrawdownReducer` + `GovernorMachine`) that was built across
> iter-20→23, but **only the position-lifecycle slice is wired in.** Everything
> time/price/equity-driven (drawdown, governor, SL/TP exit, daily/weekly resets,
> breach watchdog) bypasses the kernel and runs imperatively in services — and the
> **backtest path is a stale fork that places zero trades and enforces zero
> constraints.** The thing we use to benchmark FTMO does not actually trade.

---

## 1. The intended pipeline

```
venue/transport (IBrokerAdapter: CTrader NetMQ | SimulatedBroker | BacktestReplay)
  → market events (ticks, bars, executions, account updates)
    → IndicatorSnapshotService  (per-(symbol,timeframe,strategy) indicator values)
      → strategy.Evaluate(MarketContext) → TradeIntent
        → SignalGate.Check       (re-entry / cooldown guard)
          → OrderDispatcher → RiskManager.ValidateOrder  (sizing + DD floors + budget)
            → broker.SubmitOrder → ExecutionEvent
              → PositionTracker → EngineReducer.Apply → effects
                → EffectExecutor (PnL, RegisterRisk, PublishTradeClosed, journal)
                  → persistence + EquitySnapshot + DecisionJournal
```

## 2. How the moving parts are glued (the parts that DO work, live path)

### Multi-symbol / multi-timeframe / multi-indicator
- Bars arrive per `(Symbol, Timeframe)`. `IndicatorSnapshotService` keeps a bar buffer
  per `(symbol→timeframe→List<Bar>)`, capped at 500.
- Indicators are computed per bar and keyed by the **full signature**
  `(symbol, timeframe, type, period, stddev, param1, param2)` via
  `IndicatorCache.BuildKey()` (iter-23 E1 fix) — no cross-strategy bleed.
- Per bar, `BuildBarSnapshot(symbol)` returns `Timeframe → IReadOnlyList<Bar>`, and
  `BuildStrategyIndicatorValues(symbol, strategy)` returns the dict **that strategy**
  needs. `RegimeDetector.Detect` then picks a `MarketRegime`, and
  `StrategyBank.GetActive(symbol, tf, regime)` returns the strategies allowed to run.
- Each active strategy gets a `MarketContext(symbol, closeTick, barSnapshot,
  strategyIndicators, now)` and returns an optional `TradeIntent`.
- `EngineWorker.ProcessBarsAsync` (live) does all of this correctly.

### Multi-position lifecycle (the kernel — this part is genuinely good)
- `EngineState.Positions : Guid → PositionState` is the source of truth for positions.
- `PositionLifecycle.Apply` is a pure FSM:
  `Intended → Submitted → Open → (Reducing) → Closing → Closed | Rejected`.
- `PositionTracker` is the glue: `TrackOrder` applies `OrderSubmitted`;
  `OnExecutionAsync` translates a broker `ExecutionEvent` into `OrderFilled /
  OrderPartiallyFilled / OrderRejected`, runs `EngineReducer.Apply`, and dispatches the
  returned **effects** (`RegisterRisk`, `DeregisterRisk`, `PublishTradeClosed`,
  `RecordDecisionEvent`, `CloseOpenPosition`) through `IEffectExecutor`.
- Trailing-stop math (`TrailAtr`, `TryBreakeven`, `TrailStructure`, `TrailSteppedR`,
  `TrailStepPips`) lives in `PositionLifecycle` as pure helpers.

### Constraints — hard limits and smart avoidance (live path only)
Three layers, all in the **live** path:
1. **Pre-trade gate** — `RiskManager.Validate` (called inside `ValidateOrder`):
   governor veto, protection mode, daily/max DD limit reached, max concurrent
   positions, per-strategy max positions, total exposure, news window, weekend.
2. **Smart avoidance (block before breach)** — `RiskManager.ValidateOrder`:
   worst-case projection (`candidateLoss + openLosses`) against the daily floor
   (`DailyStartEquity·(1−MaxDailyDD)`) and the max floor; plus **budget downsizing**
   (halves lots until within remaining daily budget) and drawdown-scaled sizing
   (`DrawdownScaler` / `SizeModifierPipeline`).
3. **Breach watchdog (flatten)** — `AccountProcessor.HandleAsync`: when
   `DailyDrawdownUsed ≥ MaxDailyLossPercent · FlattenAtFraction` (or max-DD equivalent),
   it calls `EnterProtectionMode` and `PositionTracker.RequestForceCloseAllAsync`,
   which force-closes every open position via the reducer. Also drives daily/weekly/
   monthly resets and emits the `EquitySnapshot` consumed by the trading loop.

`RiskManager.UpdateEquityLevels` folds each equity tick through `DrawdownReducer.Apply`,
tracking peak, daily/weekly/monthly/max drawdown, and drawdown velocity/acceleration.

---

## 3. What is actually broken / scattered (the real problem)

### 3.1 Two trading loops, diverged — backtest is a dead fork
`EngineWorker.ProcessBarsAsync` (live) and `BacktestDriver.RunAsync` (backtest) are
copy-paste forks of one concept that drifted. In the **backtest** path today:

| Concern | Live (`EngineWorker`) | Backtest (`BacktestDriver`) |
|---|---|---|
| Indicators to strategy | `IndicatorSnapshotService` (E1-correct) | `BuildIndicatorSnapshot` just `Clear()`s; the dict passed to strategies (`_reusableIndicatorDict`) is **never populated** → strategies see `{}` (`BacktestDriver.cs:125,145,305-308`) |
| Equity / dispatch | real `_currentEquity` via `AccountProcessor` callback | `_currentEquity` is `readonly`, `Balance=0`, never written → `if (Balance==0) skip` fires every bar → **no order ever placed** (`BacktestDriver.cs:171,327`) |
| Signal gate | `SignalGate.Check` | not called |
| Account updates | pumped through `AccountProcessor` every update | `HandleAccountUpdate` does only `UpdateEquityLevels` |
| Breach watchdog / resets | `AccountProcessor` every update | `AccountProcessor.HandleAsync` runs **once at init**, never again |
| Spread | symbol registry | hardcoded `0.0001m` |
| SL/TP exit | none (broker server-side) | imperative bar low/high loop (`:198-219`) |

Net: the backtest produces **0 trades and enforces 0 constraints.** The FTMO benchmark
instrument is non-functional.

### 3.2 The kernel is only half-wired
`EngineReducer.Apply` is only ever called from `PositionTracker`, and only with
`OrderSubmitted`, execution-derived events, and `ForceCloseAllRequested`. Therefore:
- `BarClosed`, `EquityObserved`, `TickReceived` are **never constructed anywhere** →
  the reducer branches `HandleBarClosed` (incl. `GovernorMachine.ApplyBar` and
  `DetectSlTpExit`), `HandleEquityObserved`, `HandleTickReceived` are **dead**.
- `DayRolled`/`WeekRolled` are published to the **EventBus**, not fed to the reducer →
  `HandleDayRolled`/`HandleWeekRolled` are **dead**.
- Consequently `EngineState.Drawdown` and `EngineState.Governor` (inside
  `PositionTracker._state`) are frozen at `Empty` and never reflect reality.

### 3.3 Same concept, multiple representations (scatter)
- **Drawdown** lives in 4 places: `EquitySnapshot.CurrentDailyDrawdown/MaxDrawdown`
  (computed in `AccountProcessor`), `RiskManager.Drawdown` (`DrawdownState`),
  `RiskManager.CurrentState` (`ExtendedRiskState` mirror), and the dead
  `EngineState.Drawdown`.
- **Limits** come from **two** config sources that overlap and can disagree:
  `RiskProfile` (`MaxDailyDrawdownPercent`, `MaxTotalDrawdownPercent`,
  `RiskPerTradePercent`, `MaxExposurePercent`, `MaxConcurrentPositions`) **and**
  `PropFirmRuleSet` (`MaxDailyLossPercent`, `MaxTotalLossPercent`, `DailyDdBase`).
  `Validate` checks against the RuleSet; `ValidateOrder` worst-case checks against the
  Profile.
- **Governor** is queried imperatively in `RiskManager.Validate` (`governor.Evaluate`)
  **and** has a parallel dead `GovernorMachine.ApplyBar` in the kernel.
- **SL/TP exit** detection exists imperatively in `BacktestDriver:198-219`, as the dead
  `EngineReducer.DetectSlTpExit`, and as `PositionTracker.DetermineExitReason`.

### 3.4 Live-path concurrency hazard (FIXED — iter-24)
**Was** — in live/paper mode several `Task.WhenAll` tasks mutated `PositionTracker` concurrently:
- `ProcessTicksAsync` drains `_executionEventChannel` → `PositionTracker.OnExecutionAsync`.
- `ProcessBarsAsync` → `TradingLoop.ProcessBarAsync` → `MarketEventSource.DrainExecutionStreamAsync`,
  which **also** drains `_executionEventChannel` (and `broker.ExecutionStream`) →
  `PositionTracker.OnExecutionAsync`.

`PositionTracker` holds non-thread-safe state (`EngineState.Positions` dictionary,
`_processedExecutionIds` HashSet, `_pendingIntent`). Concurrent execution handling can corrupt
it. The channel is even declared `SingleReader = true` while having two readers — a contract
violation. (Backtest is single-threaded, so it's unaffected — which is why tests stay green.)

**Fixed by:** (1) **lean tick hot path** — `ProcessTicksAsync` now only translates ticks and never
touches `PositionTracker`; (2) **single execution consumer** — `MarketEventSource.ConsumeExecutionsAsync`
is the one reader of `_executionChannel` in live (pairs with the single-writer `ProcessExecutionEventsAsync`),
and `TradingLoop` no longer drains executions at all (backtest caller drains synchronously;
`marketEvents`/`strategies` dropped from its ctor); (3) **serialized tracker** — `PositionTracker`
guards its three mutators (`TrackOrder`, `OnExecutionAsync`, `RequestForceCloseAllAsync`) with a
`SemaphoreSlim(1,1)`, uncontended in single-threaded backtest; `EffectExecutor` does not re-enter it.

**Residual (agent):** add a live-path concurrency stress test (many fills + force-close racing
`TrackOrder`) to lock the invariant in.

### 3.4b Venue / backtest-mode coupling in the engine (PARTIALLY FIXED — iter-24)
The engine core (`EngineRunner`) used to **type-sniff concrete adapters** — `is CTraderBrokerAdapter`
(reconnect handler + lock-step `CompleteBarAsync(CurrentBarSeq)`), `is SimulatedBrokerAdapter`
(`OnTickReceived`), `is BacktestReplayAdapter` (`SyncToBar`). That coupled the engine to specific
venue implementations and to backtest internals.

**Fixed:** these four behaviours are now **default-no-op methods on `IBrokerAdapter`** —
`RegisterConnectedHandler`, `OnTickObserved`, `OnBarObserved`, `CompleteBarAsync(ct)` — each
overridden by the venue that needs it. `EngineRunner` calls them polymorphically and **no longer
references any concrete adapter type**.

**Residual (agent) — the deeper one:** `EngineRunner` still branches `if (_engineMode == Backtest)`
into two top-level loops — `RunBacktestLoopAsync` (bar-stepped, synchronous fills, per-bar account
pump, lock-step) vs the live `Task.WhenAll` of async stream pumps. This is a real *pacing* difference,
but it's the last big backtest coupling. Target: an `IEnginePacer` / venue-drive abstraction that
owns pacing (the venue decides bar-stepped vs async) so the engine runs one path. Also: `AccountProcessor`
still takes `EngineMode` only to stamp `EquitySnapshot.Mode` — push that onto the snapshot source.
`DataFeedService` still sniffs `SimulatedBrokerAdapter` 3× (feed-side, lower priority).

### 3.5 Known smaller defects found while modeling
- `OrderDispatcher.DispatchAsync` is called with `openPositions: []` from **both**
  loops (`EngineWorker.cs:313`, `BacktestDriver.cs:178`) → the worst-case portfolio DD
  projection is blind to actually-open positions.
- `RiskProfile.MaxDailyDrawdownPercent` / `MaxTotalDrawdownPercent` are `double` cast to
  `decimal` at every use (precision smell on money math).
- `DrawdownReducer.ApplyMonthlyReset` exists but no `MonthRolled` reducer path exists.

### 3.6 Venue / account / PnL audit (findings only — iter-24, not yet fixed)
Audit of account snapshotting, cTrader wiring, and how positions track the latest venue-confirmed
state. Ordered by money/data risk. **All are findings for the agent — no code changed.**

**Money accuracy (highest):**
- **M1 — venue PnL is discarded; recorded PnL ignores costs. ✅ FIXED (iter-24).** `PublishTradeClosed`
  now carries optional `GrossProfit/NetProfit/Commission/Swap`; `PositionTracker.OnExecutionAsync`
  enriches the close effect from the `ExecutionEvent`, and `EffectExecutor` prefers the venue figures
  (commission/swap-inclusive net) when present, recomputing gross only for the simulated venue (null PnL).
  Trade ledger now matches account equity on the live venue. (Residual: simulated/backtest venue still
  has no commission/swap model — fine, it reports null.)
- **M2 — disconnected close writes a synthetic fill at `Price(1.0)`** (`CTraderBrokerAdapter.cs:347`).
  If that exec is processed, PnL is computed against 1.0 = garbage. Mark it non-PnL or skip ledger.

**Venue ↔ engine state (high):**
- **V1 — no startup/reconnect position reconciliation.** `GetAccountStateAsync` returns `(0,0,[])`
  (`CTraderBrokerAdapter.cs:419`); `EngineRunner` only logs `OpenPositions.Count` and never seeds
  `PositionTracker` from venue-open positions. After a restart/reconnect with live positions the engine
  is blind to them — can't manage, trail, or force-close them on breach. Real money risk.
- **V2 — Guid clientOrderId vs venue positionId.** The engine keys everything by its own `Guid` and
  sends it as `positionId` on close/modify; the Guid↔cTrader-position-id map lives only in the cBot and
  is lost on reconnect → existing positions become un-closeable. Needs a durable mapping + resync.
- **V3 — SL/TP modifications are fire-and-forget.** `ModifyOrderAsync` is buffered with no confirmation;
  verify the venue-confirmed SL is written back to `PositionState.CurrentStopLoss` (trailing). If not,
  the engine's risk/exit view drifts from the venue, and backtest `SimulateBarExits` exits on a stale SL.
- **V4 — exec dedup full-clears at 500. ✅ FIXED (iter-24).** Replaced the `HashSet.Clear()` with a
  bounded LRU (`_recentExecOrder` queue) that evicts the single oldest signature, so a re-sent
  duplicate is still caught instead of slipping through.
- **V5 — buffered commands lost on mid-bar disconnect**: `_bufferedCommands` flush only on
  `CompleteBarAsync`; a disconnect before bar-done drops queued orders/closes (only `_pendingCommands`
  re-flush). Dropped-order risk.

**Account snapshot / resets (medium):**
- **A1 — reset boundaries keyed on `_clock.UtcNow`**, not the account update's timestamp
  (`AccountProcessor.cs:74-77`) → daily/weekly DD baseline can latch at the wrong instant → FTMO
  daily-loss miscalc (matters most live, where engine clock ≠ venue time).
- **A2 — daily-reset baseline is always `update.Equity`** (`OnDailyReset`) regardless of `DailyDdBase`;
  positions spanning midnight bake floating PnL into the new daily-start → wrong daily-DD denominator.
- **A3 — first account update with Balance==0** initializes drawdown base 0 (`AccountProcessor.cs:48`)
  → `ValidateBudgetEntry` returns false (blocks all trades) / divide-by-zero risk until a real balance
  arrives. cTrader's empty `GetAccountStateAsync` makes this the normal startup path.
- **A4 — FloatingPnL defined two ways**: `equity−balance` (bar / bar_result) vs the explicit
  `floatingPnL` field (acct sub-message) — drift if venue `equity` excludes floating.

---

## 4. The unification thesis (what "strong core" means here)

1. **One trading loop.** Extract the per-bar evaluate→gate→dispatch→drain logic into a
   single `TradingLoop`/`BarProcessor` shared by live and backtest. Delete
   `BacktestDriver`. The *only* legitimate live/backtest differences are venue concerns
   and belong behind the broker adapter + clock: SL/TP fill simulation, lock-step
   `CompleteBarAsync`, and the account-update feed. A green backtest then also proves
   the live path.
2. **One source of truth per concept.** Drawdown, governor, and position state should be
   read from one place. Either finish wiring the kernel (feed `EquityObserved`/
   `BarClosed`/`DayRolled` through the reducer and delete the imperative copies) or
   formally designate `RiskManager` as the owner and delete the dead kernel branches —
   but not both half-done.
3. **One limit config.** Collapse `RiskProfile` vs `PropFirmRuleSet` overlap to a single
   resolved limit set used by gate, worst-case projection, and watchdog alike.
4. **Constraints proven by tests, not logs.** Deterministic FTMO fixtures that walk
   equity into the daily-loss line, the max-loss line, and the profit target, asserting
   on `DrawdownState` / journal — over a backtest that actually trades.

See `PLAN.md` for the phased path.
