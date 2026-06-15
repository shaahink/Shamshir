# Shamshir — System Model (as-built, 2026-06-15)

Purpose: model what this engine **actually is today**, how the logic is glued across
multiple symbols, timeframes, indicators, and positions, and exactly where it stays
true to constraints vs. where it silently does not. Written before the iter-24 unify
work so we agree on the territory.

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

### 3.4 Known smaller defects found while modeling
- `OrderDispatcher.DispatchAsync` is called with `openPositions: []` from **both**
  loops (`EngineWorker.cs:313`, `BacktestDriver.cs:178`) → the worst-case portfolio DD
  projection is blind to actually-open positions.
- `RiskProfile.MaxDailyDrawdownPercent` / `MaxTotalDrawdownPercent` are `double` cast to
  `decimal` at every use (precision smell on money math).
- `DrawdownReducer.ApplyMonthlyReset` exists but no `MonthRolled` reducer path exists.

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
