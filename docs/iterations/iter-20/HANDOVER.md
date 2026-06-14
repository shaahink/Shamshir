# Iteration 20 — HANDOVER.md

**Branch**: `iter/20-engine-core` (off `iter/19-fixes`, commit `d46e7e2`)  
**Implemented**: 2026-06-14  
**Status**: Core refactor complete — 193 tests green, 35/36 simulation pass. PositionLifecycle FSM built, EngineMode confined, venue/transport split, Experiments extracted.  
**Plan**: `docs/iterations/iter-20/PLAN.md`

---

## Delivery Summary: 16 commits across 11 phases

| # | Commit | Phase |
|---|--------|-------|
| 1 | `1049308` | P0 — `TradingEngine.Engine` project + 3 architecture guardrails |
| 2 | `02b2f1c` | P1 — `DecisionRecord` + `IDecisionJournal` unified journal; all 3 sinks routed; EF migration |
| 3 | `278d567` | P2 — `AccountSnapshot` + `IEquitySink`; backtest equity captured; EngineMode equity branch removed |
| 4 | `4c6810b` | P3 — `DrawdownReducer` (pure) + `RiskGate.ProjectWorstCase` simultaneous-stop projection integrated into OrderDispatcher |
| 5 | `5a02abe` | P4 — `PositionPhase` + `PositionState` + `PositionLifecycle` FSM; 19 cell unit tests + 7 characterization goldens |
| 6 | `b36348f` | P5 — `EngineState` aggregate + `EngineReducer.Apply` chokepoint; event-script tests |
| 7 | `2f6429e` | P6 — EngineMode removed from PositionTracker; architecture test unskipped (3/3) |
| 8 | `22e20e1` | P7 — `RunProjection` — dashboard reads unified journal + snapshots |
| 9 | `2b78e3b` | P8 — `tests/README.md` documenting 4-layer test taxonomy |
| 10 | `09e0ea8` + `4b08a98` | P9 — Host de-bloat: persistence handlers, PipelineEventWriter, ConfigLoader → Infrastructure; SymbolCatalog → Application; BarEvaluationHandler → Infrastructure |
| 11 | `0d1f99b` | P10 — `IMessageTransport` — cTrader venue split from NetMQ; NetMQ in exactly 1 file (`NetMqMessageTransport.cs`) |
| 12 | `121c21e` | P11 — `TradingEngine.Experiments` extracted via `IExperimentHostFactory` interface; `EngineHostOptions`/`BacktestProgressEvent`/`LoadedConfig` moved to Domain |
| 13 | `06fee62` | P10 test — Fake `IMessageTransport` proves `CTraderBrokerAdapter` testable without NetMQ |
| 14 | `0e2937a` | P4d — `PositionTracker.OnExecutionAsync` delegates to `PositionLifecycle.Apply()`; 7 characterization goldens preserved |
| 15 | `34e7b81` | Fix — register `IDecisionJournal` + `IEquitySink` in test harness DI |

---

## Test Results

| Suite | Count | Status |
|-------|-------|--------|
| Unit | 165 | All pass |
| Integration | 20 | All pass |
| Architecture | 3 | All pass (EngineMode confined, Engine pure) |
| Simulation (characterization) | 7 | All pass |
| Simulation (full pipeline) | 35/36 | 35 pass, 1 timeout (`NetMQBridgeTest` — requires cTrader process) |

### Run commands
```
dotnet test tests/TradingEngine.Tests.Unit
dotnet test tests/TradingEngine.Tests.Integration
dotnet test tests/TradingEngine.Tests.Architecture
dotnet test tests/TradingEngine.Tests.Simulation        # takes ~6 min
dotnet test tests/TradingEngine.Tests.Simulation --filter "FullyQualifiedName~PositionLifecycleGoldenTests"
```

---

## Project Layout (After)

```
Domain ← Engine ← Application ← Infrastructure ← Experiments
                    ↑                              ↑
                    └── Host ───────────────────────┘
```

### `TradingEngine.Engine` (NEW — pure decision kernel)
- `DecisionRecord.cs`, `EngineDecision.cs` (Domain)
- `PositionLifecycle.cs` — FSM: Intended → Submitted → Open → Reducing/Closing → Closed/Rejected
- `DrawdownReducer.cs` — Pure `(DrawdownState, equity) → DrawdownState`
- `RiskGate.cs` — `ProjectWorstCase` simultaneous-stop DD projection
- `EngineReducer.cs` — Composes PositionLifecycle + DrawdownReducer; `Apply(EngineState, EngineEvent) → EngineDecision`

### `TradingEngine.Experiments` (NEW)
- `ExperimentRunner`, `ExperimentCli`, `ConfigOverrideApplier`, `VariantScorer`, `WalkForwardSplitter`, `ExperimentReportWriter`
- Depends on `IExperimentHostFactory` (in Application) — no Host dependency
- `ExperimentHostFactoryAdapter` in Host implements the interface

### Infrastructure (Reorganized)
```
Infrastructure/
  Configuration/    ConfigLoader.cs
  Events/           PipelineEventWriter.cs, TypedEventBus.cs
  Persistence/      EquityPersistenceHandler.cs, TradePersistenceHandler.cs,
                    ProtectionLedgerPersistenceHandler.cs, BarEvaluationHandler.cs
  Transport/NetMq/  NetMqMessageTransport.cs        ← ONLY file with `using NetMQ`
  Venues/
    CTrader/        CTraderBrokerAdapter.cs          ← broker semantics
    Simulated/      SimulatedBrokerAdapter.cs        ← in-process backtest
```
Deleted: `NetMQBrokerAdapter.cs`, `TradingEngine.Infrastructure.Adapters/SimulatedBrokerAdapter.cs`

### Domain (Types Added)
- `DecisionRecord`, `EngineDecision`, `EngineState`
- `PositionPhase` enum, `PositionState` record
- `EngineEvent` base + `OrderSubmitted`, `OrderFilled`, `OrderPartiallyFilled`, `OrderRejected`, `CloseRequested`, `BarClosed`, `TickReceived`, `EquityObserved`
- `EngineEffect` base + `SubmitOrder`, `ModifyStopLoss`, `CloseOpenPosition`, `RecordDecisionEvent`
- `IDecisionJournal`, `IEquitySink`, `IAccountSnapshotStore`, `IMessageTransport`
- `AccountSnapshot`, `DrawdownState`
- `EngineHostOptions`, `BacktestProgressEvent`, `LoadedConfig`, `StrategyConfigEntry` (moved from Host/Infrastructure)

### Host (Slimmed)
Remaining: `Program.cs`, `EngineHostFactory.cs`, `EngineWorker.cs`, `EngineWorkerDependencies.cs`, `ExperimentHostFactoryAdapter.cs`, `DataFeedService.cs`, `DailyResetService.cs`, `StrategyRegistry.cs`, `StrategyBankService.cs`, `CrossRateStore.cs`, `BacktestProgressEvent.cs` (moved to Domain), `GlobalUsings.cs`

---

## What the Architecture Tests Enforce

| Test | What it checks | Status |
|------|---------------|--------|
| `Engine_references_only_Domain` | `TradingEngine.Engine` assembly depends only on `TradingEngine.Domain` + BCL | Pass |
| `Engine_has_no_ILogger_no_DateTimeNow` | No `ILogger`, `DateTime.UtcNow`, EF types in Engine | Pass |
| `EngineMode_only_in_host_and_infrastructure` | `EngineMode` enum referenced only from Host, Infrastructure, test projects | Pass |

---

## What the PositionLifecycle FSM Covers

| Phase | x Event | → Phase | Effects |
|-------|---------|---------|---------|
| Intended | OrderSubmitted | Submitted | — |
| Intended | OrderRejected | Rejected | — |
| Submitted | OrderFilled (full) | Open | — |
| Submitted | OrderFilled (partial) | Submitted | — |
| Submitted | OrderRejected | Rejected | — |
| Open | OrderFilled (full close) | Closed | — |
| Open | OrderFilled (partial close) | Reducing | — |
| Open | CloseRequested | Closing | `CloseOpenPosition` |
| Reducing | OrderFilled | Open/Closed | — |
| Closing | OrderFilled | Reduc./Closed | — |
| Closed / Rejected | any | unchanged | — |
| any invalid | any | unchanged | `RecordDecisionEvent("IllegalTransition")` |

**PositionTracker.OnExecutionAsync now delegates to this FSM** for phase transitions. The 4 legacy dictionaries (`_pendingOrders`, `_openPositions`, `_pendingRisk`, `_processedExecutionIds`) remain as the external contract.

---

## Remaining Work (Not Done — Deferred)

### HIGH — Complete PositionLifecycle FSM (Items 1b/1c/1g)

`PositionManager` still owns 5 trailing/breakeven methods (`StepPips`, `AtrMultiple`, `Structure`, `SteppedR`, `BreakevenThenTrail`). These need to be ported into `PositionLifecycle` as pure functions.

**Required steps:**
1. Add tracking fields to `PositionState`: `HighWater`, `LowWater`, `BreakevenApplied`, `InitialSlDistance`
2. Port the 5 `TrailingHelpers` methods to work with `PositionState` (currently they use `Position`)
3. Wire into `HandleOpenBar`/`HandleOpenTick` to emit `ModifyStopLoss` effects
4. Unit-test trailing cells (breakeven fires at R-multiple, step trail moves SL on favorable moves, etc.)
5. After delegation, delete `PositionLifecycleState` enum (still used by `PositionManager`)

**Files to modify**: `PositionState.cs`, `PositionLifecycle.cs`, `PositionManager.cs`, `TrailingHelpers.cs`  
**Gate**: 7 characterization goldens must stay green

### HIGH — Complete EngineReducer (Item 2)

`EngineReducer.Apply()` exists but only handles position lifecycle + drawdown. It does NOT handle:
- SL/TP bar-level exit check (currently inline in `EngineWorker.RunBacktestLoopAsync` lines 523-546)
- Force-close on risk breach
- Risk registration/deregistration as effects
- Signal gate interactions
- Governor callbacks (`OnTradeClosed`, `OnBar`)
- Account resets (daily/weekly/monthly)
- PnL computation on close
- Trade result publishing (`TradeClosed` event)
- Journal/progress reporting as structural effects

The plan's vision: `EngineWorker` becomes a thin pump — *receive event → EngineReducer.Apply → execute effects*.  
Current state: EngineWorker still has ~15 capabilities that EngineReducer lacks.

**Approach**: After Item 1 is complete, decide whether to fill EngineReducer gaps or keep the existing loop with the FSM underneath.

### MEDIUM — NetMQBridgeTest (Infrastructure)

`NetMQBridgeTest.EngineReceivesBarAndTickOverNetMQ` times out — requires a running cTrader instance with NetMQ connectivity. This is an environment dependency, not a code regression. Run with cTrader installed on the test machine.

### LOW — Cleanup

- Delete empty `Experiments/` directory under Host
- BacktestJournal decoupled from `DecisionRecord` flow (still uses direct `IProgress<BacktestProgressEvent>`)
- Host file count still ~15 (target was 5: Program, EngineHostFactory, EngineWorkerDependencies, EngineWorker, GlobalUsings)

---

## Key Architectural Decisions

1. **Conservative delegation**: `PositionTracker` delegates phase transitions to `PositionLifecycle.Apply()` but keeps its dictionaries as the external API. This preserves the 7 characterization goldens while introducing FSM logic.

2. **`IExperimentHostFactory` over static calls**: `ExperimentRunner` depends on the interface (in Application), not `EngineHostFactory` (in Host). `ExperimentHostFactoryAdapter` bridges them. This broke the circular dependency that blocked Phase 11.

3. **Types moved to Domain**: `EngineHostOptions`, `BacktestProgressEvent`, `LoadedConfig` moved to Domain so Application can reference them without depending on Host or Infrastructure.

4. **Venue/Transport split**: `IMessageTransport` (Domain) → `NetMqMessageTransport` (transport) → `CTraderBrokerAdapter` (venue). NetMQ is now referenced in exactly 1 file (`NetMqMessageTransport.cs`). Venue logic is testable with `FakeMessageTransport`.

5. **`EngineMode` confined**: The architecture test `EngineMode_only_in_host_and_infrastructure` passes. `PositionTracker` no longer takes `EngineMode`. `TradeResult.Mode` defaults to `Backtest`.

---

## Files Added (This Iteration)

```
src/TradingEngine.Engine/
  EnginePlaceholder.cs, GlobalUsings.cs, PositionLifecycle.cs,
  DrawdownReducer.cs, RiskGate.cs, EngineReducer.cs

src/TradingEngine.Experiments/
  TradingEngine.Experiments.csproj, GlobalUsings.cs,
  ConfigOverrideApplier.cs, ExperimentCli.cs, ExperimentReportWriter.cs,
  ExperimentRunner.cs, VariantScorer.cs, WalkForwardSplitter.cs

src/TradingEngine.Domain/
  DecisionRecord.cs, EngineDecision.cs, EngineHostOptions.cs,
  LoadedConfig.cs, BacktestProgressEvent.cs
  Events/EngineEffects.cs
  Interfaces/IDecisionJournal.cs, IEquitySink.cs, IAccountSnapshotStore.cs,
    IMessageTransport.cs
  PositionManagement/PositionPhase.cs, PositionState.cs
  RiskAndEquity/AccountSnapshot.cs, DrawdownState.cs, EngineState.cs

src/TradingEngine.Infrastructure/
  Configuration/ConfigLoader.cs (moved)
  Events/PipelineEventWriter.cs (moved)
  Persistence/EquityPersistenceHandler.cs, TradePersistenceHandler.cs,
    ProtectionLedgerPersistenceHandler.cs, BarEvaluationHandler.cs (moved)
  Transport/NetMq/NetMqMessageTransport.cs
  Venues/CTrader/CTraderBrokerAdapter.cs
  Venues/Simulated/SimulatedBrokerAdapter.cs (moved)

src/TradingEngine.Application/
  IExperimentHostFactory.cs, GlobalUsings.cs, SymbolCatalog.cs (moved)

src/TradingEngine.Host/
  ExperimentHostFactoryAdapter.cs

tests/
  TradingEngine.Tests.Architecture/EnginePurityTests.cs
  TradingEngine.Tests.Simulation/Characterization/PositionLifecycleGoldenTests.cs
  TradingEngine.Tests.Unit/Infrastructure/FakeTransportTests.cs
  TradingEngine.Tests.Unit/Phase3BTests/
    PositionLifecycleTests.cs, DrawdownReducerTests.cs, RiskGateTests.cs,
    EngineReducerTests.cs, BufferedEquitySinkTests.cs
  TradingEngine.Tests.Integration/InfrastructureTests/UnifiedDecisionJournalTests.cs
  README.md
```

## Files Deleted

```
src/TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs
src/TradingEngine.Domain/Events/EngineEvents.cs (merged into EngineEvent.cs)
```

---

## Next Session Instructions

1. Run `dotnet test tests/TradingEngine.Tests.Architecture` — all 3 must pass
2. Run `dotnet test tests/TradingEngine.Tests.Unit` — all 165 must pass
3. Start with **Item 1b**: read `TrailingHelpers.cs`, port the 5 trailing methods to `PositionLifecycle` using `PositionState`
4. Unit-test each trailing cell in isolation
5. Wire into `HandleOpenBar`/`HandleOpenTick`
6. After delegation, delete `PositionLifecycleState` enum
7. Run 7 characterization goldens after every change
8. Run full 36 simulation suite before declaring done

**Prime directive**: 7 characterization golden tests are the behavioral contract. They must stay green at every step. If a step turns them red, stop and fix — do not push red.
