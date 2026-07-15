# Iter-23 Handover — 2026-06-15

**Branch**: `iter/23-close-gap` (15 commits on top of `iter/22-consolidation`)
**Test status**: Architecture 3/3, Unit 163/163, Goldens 7/7 — green at every commit

---

## What was delivered

### Group A — Extraction (4 phases)
| Phase | Result |
|---|---|
| A1 | Moved `DailyResetService`, `CrossRateStore` to `TradingEngine.Application`. 3 services (StrategyRegistry, StrategyBankService, DataFeedService) kept in Host — circular deps or Infrastructure deps block moving. |
| A2 | Extracted `MarketEventSource` from EngineWorker: stream readers (ticks, bars, accounts, executions), drain loop, account queue. |
| A3 | Extracted `IndicatorSnapshotService`: all indicator computation, bar storage, snapshot building with per-strategy key isolation. |
| A4 | EngineWorker reduced from **635 → 338 lines** (-47%). Account update processing moved to `AccountProcessor`. Remaining 338 lines are constructor wiring + ProcessBarsAsync (trading loop). **Target ≤150 not reached** — ProcessBarsAsync still inline. |

### Group B — EngineReducer completion (4 phases)
| Phase | Effect added | Old manual code removed |
|---|---|---|
| B1 | `PublishTradeClosed(TradeResult)` on close transition | PnL computation + TradeClosed publishing from PositionTracker |
| B2 | `RegisterRisk` / `DeregisterRisk` on open/close | `RiskManager.RegisterPosition` / `DeregisterPosition` from PositionTracker |
| B3 | `CloseOpenPosition(reason: Breach)` via `ForceCloseAllRequested` handled in reducer | `ConsumeForceClosePending()` polling — **method deleted** from IRiskManager and RiskManager |
| B4 | `RecordDecisionEvent` on every transition (accept, reject, open, partial, close, breach, illegal) | — |

### Group C — Composition (1 phase)
| Phase | Result |
|---|---|
| C1 | Per-module `AddXxx` extension methods: `AddMarketData()`, `AddRisk()`, `AddPersistence()`, `AddStrategies()`, `AddEventInfrastructure()`, `AddEngineWorker()`. Program.cs collapsed from **311 → 44 lines**, DI registrations from **52 → 2**. WireEventHandlers/WireRiskRules moved to extension methods. Scrutor package added (not yet wired for assembly scanning). |

### Group D — Test harnesses (1 phase)
| Phase | Result |
|---|---|
| D1 | Deleted `EngineTestHarness` (0 tests) and `DrawdownTestHarness` (migrated 5 tests to call `RiskManager` directly). `CtraderTestHarness` (8 tests) and `ReplayTestHarness` (1 test) kept — need BrokerAdapter abstraction to migrate. |

### Group E — Indicator correctness (1 phase)
| Phase | Result |
|---|---|
| E1 | Indicator values keyed by full signature `(symbol, timeframe, type, period, stddev, param1, param2)` via `IndicatorCache.BuildKey()`. Per-strategy `IndicatorValues` dictionaries — no cross-strategy bleed. 6 unit tests verify key uniqueness. |

### Group F — Tick hotpath (1 phase)
| Phase | Result |
|---|---|
| F1 | Tick handler stripped: per-tick `LogDebug("TICK|…")` guarded behind `LogLevel.Trace`. Account update polling removed from tick loop — dedicated `ProcessAccountQueueAsync` task. |

### Group G — Risk consolidation (3 phases)
| Phase | Result |
|---|---|
| G1 | **`DrawdownTracker` class deleted**. All code uses immutable `DrawdownState` + `DrawdownReducer` (velocity tracking, DailyDdBaseMode support added to DrawdownReducer). `RiskManager` holds a `DrawdownState` directly. `IRiskManager.Drawdown` property exposed. |
| G2 | Three validation entry points (Validate, RiskGate.ProjectWorstCase, ValidateBudgetEntry) collapsed into one: `IRiskManager.ValidateOrder()`. `OrderDispatcher` calls a single method. `ProjectedPosition` type moved from Engine to Domain. |
| G3 | Governor state updated via EngineReducer: `GovernorMachine.ApplyBar()` on BarClosed, `ApplyDailyReset()` on DayRolled. Removed direct `_governor?.OnBar()` and `_governor?.OnDailyReset()` calls from EngineWorker and BacktestDriver. DayRolled/WeekRolled events now published from account processing. |

---

## Architecture changes

### New files created
```
src/TradingEngine.Domain/Interfaces/IEffectExecutor.cs
src/TradingEngine.Domain/RiskAndEquity/ProjectedPosition.cs
src/TradingEngine.Host/IndicatorSnapshotService.cs
src/TradingEngine.Host/MarketEventSource.cs
src/TradingEngine.Host/AccountProcessor.cs
tests/TradingEngine.Tests.Unit/Infrastructure/IndicatorCacheKeyTests.cs
```

### Files deleted
```
src/TradingEngine.Risk/DrawdownTracker.cs
tests/TradingEngine.Tests.Simulation/Harness/EngineTestHarness.cs
tests/TradingEngine.Tests.Simulation/Harness/DrawdownTestHarness.cs
tests/TradingEngine.Tests.Unit/RiskTests/DrawdownTrackerTests.cs
tests/TradingEngine.Tests.Unit/Risk/DrawdownTrackerExtendedTests.cs
tests/TradingEngine.Tests.Simulation/Risk/WeeklyDDProtectionTests.cs
```

### Key modifications
- `PositionTracker.cs`: Constructor simplified (removed `runContext`, `governor` params). Effects from `EngineReducer.Apply` routed to `IEffectExecutor` — no more manual PnL/risk publishing.
- `OrderDispatcher.cs`: Single `ValidateOrder()` call replaces three separate validation steps.
- `RiskManager.cs`: No longer takes `DrawdownTracker`. Holds `DrawdownState` internally. `ValidateOrder()` added.
- `EngineReducer.cs`: Handles `ForceCloseAllRequested`, `PublishTradeClosed` on all close transitions, `GovernorMachine.ApplyBar` on BarClosed.
- `PositionLifecycle.cs`: `RecordDecisionEvent` on every valid transition. `RegisterRisk`/`DeregisterRisk`/`PublishTradeClosed` on open/close.
- `EffectExecutor.cs`: Handles `PublishTradeClosed` (PnL computation + TradeClosed event), `RegisterRisk`, `DeregisterRisk`. Added deps: `ISymbolInfoRegistry`, `IRiskManager`, `IPositionManager`, `ITradingGovernor`, `ISignalGate`, strategies.
- `Program.cs`: Trimmed from 311 lines to 44 lines. 2 direct DI registrations remaining (down from 52).
- `EngineServiceCollectionExtensions.cs`: Refactored into 6 per-module extensions + `EngineHostFactory`-compatible wrappers.

---

## Deferred / known gaps

| Item | Reason | Suggested approach |
|---|---|---|
| EngineWorker ≤150 lines | ProcessBarsAsync (trading loop, ~120 lines) still inline | Extract `BarProcessor` or `TradingLoop` collaborator |
| StrategyRegistry/DataFeedService in Host | Circular deps: Application ↔ Strategies / Infrastructure | Use interface-based loading or reflection in StrategyRegistry |
| TradingGovernorService retirement | `GovernorSizeModifier` + `EffectExecutor.OnTradeClosed` still depend on `ITradingGovernor` | Change sizing pipeline to read `EngineState.Governor`; route TradeClosed through reducer |
| Scrutor assembly scanning not wired | Package added but `StrategyRegistry` still manually creates strategies | Replace `StrategyRegistry` factory with `services.Scan(...)` for `IStrategy`, `IEventHandler<>`, `ISizeModifier` |
| CtraderTestHarness / ReplayTestHarness | 9 tests depend on BrokerAdapter infrastructure | Create `EngineHarnessBuilder` that swaps venue adapters via the C1 `AddXxx` extensions |
| `DrawdownReducer.ApplyMonthlyReset` never called | No `MonthRolled` handler exists in EngineReducer | Add `MonthRolled` event type and handler |
| `MaxDailyDrawdownPercent`/`MaxTotalDrawdownPercent` conversion | Both are `double` in RiskProfile but used as `decimal` with casts | Normalize to `decimal` |

---

## Commit log (15 commits)

```
20eaeaf refactor(iter23-d1): delete EngineTestHarness + DrawdownTestHarness
ae32060 refactor(iter23-c1): per-module AddXxx extensions; Program.cs 44 lines
d5eb48a refactor(iter23-a2a4): extract MarketEventSource + AccountProcessor; EngineWorker 338 lines
55e61a8 refactor(iter23-a2): extract MarketEventSource from EngineWorker
dfc7661 perf(iter23-f1): lean tick hotpath
47613a0 refactor(iter23-a3): extract IndicatorSnapshotService
bee09b7 refactor(iter23-g3): GovernorMachine in kernel
6da7162 refactor(iter23-g2): single RiskGate validation path
be080ce refactor(iter23-g1): single DrawdownReducer; delete DrawdownTracker
4ed4708 refactor(iter23-a1): move DailyReset/CrossRate to Application
92e9c46 feat(iter23-b4): structural RecordDecisionEvent on every transition
d43385f feat(iter23-b3): force-close on breach as reducer effect; remove ConsumeForceClosePending
c116e5c feat(iter23-b2): risk register/deregister as reducer effects
32a9654 feat(iter23-b1): PnL + TradeClosed as reducer effects
d240abb fix(iter23-e1): indicator values keyed by full signature; dedup; via IndicatorCache
```
