# Code Map — Feature & Process to File Reference

**For**: Any agent needing to find "where is X implemented" without grepping the codebase.
**Updated**: 2026-07-16 (post iter-alpha-loop close + refactor/god-classes merge + structural-edge S0/S1.1)

> Pre-2026-07 versions of this map described the pre-iter-36 imperative engine (production
> `TradingLoop`/`OrderDispatcher`, `PipelineEvents` journal, Razor Pages UI, `NetMQBrokerAdapter`,
> kernel under `Domain/Engine`). All stale entries have been replaced below. Companion docs:
> `SYSTEM-REFERENCE.md` (what the system is), `BACKTEST-ARCHITECTURE.md` (venue paths),
> `TEST-ARCHITECTURE.md` (test tiers), and the `shamshir-kernel` / `shamshir-ctrader` skills.

---

## Part 1 — Feature Index

Find a feature → see which files implement it.

### A–C

| Feature | Key Files |
|---------|-----------|
| **Add-on packs (BE/trail/Ride/PartialTp)** | `TradingEngine.Web/Configuration/AddOnPackSeeder.cs` (seeds `runner-aggressive` etc.), `TradingEngine.Domain/PositionManagement/` (options), applied via run config (`PackId` per run-plan row) |
| **API endpoints** | `TradingEngine.Web/Api/*Controller.cs` — `RunsController` (runs/journal/duplicate/challenge-sim), `ExperimentsController`, `TradesController`, `BarsController`, `WalkForwardController`, `SweepController`, `ExitLabController`, `DataManagerController`, `SystemController` (health), full list in `SYSTEM-REFERENCE.md` §8 |
| **Auto-sync (market data to latest)** | `TradingEngine.Web/Services/AutoSyncService.cs`, `TradingEngine.Web/Api/DataManagerController.cs` |
| **Backtest orchestration** | `TradingEngine.Web/Services/BacktestOrchestrator.cs` (queue/lifecycle/finalize — decomposed 2026-07-15), run-scoped services in `...Web/Services/Runs/` (RunRegistry, RunRecordStore, RunConfigAssembler, RunMarketContextLoader, RunProgressProjector, EngineHostLifecycle), venue execution behind `...Web/Services/Venues/IVenueRunner` (ReplayVenueRunner, CTraderVenueRunner) |
| **Bar evaluation (strategies → proposals)** | `TradingEngine.Host/BarEvaluator.cs` (production), strategies in `TradingEngine.Strategies/` |
| **Breach watchdog / daily-DD guard** | `TradingEngine.Engine/Kernel/Kernel.cs` (`DecideEquity`, `EvaluateDrawdownBreach`), `TradingEngine.Host/KernelDailyDdGuardEvaluator.cs` |
| **Breakeven / trailing** | `TradingEngine.Host/KernelTrailingEvaluator.cs` → `StopLossModifyRequested` through the reducer; options in `TradingEngine.Domain/PositionManagement/` (`TrailingMethod`: StepPips/AtrMultiple/BreakevenThenTrail/Structure/SteppedR/None) |
| **cBot (cTrader side)** | `TradingEngine.Adapters.CTrader/TradingEngineCBot.cs`, `.../ShamshirTradeLogger.cs` (own resilient ledger `shamshir-report.json`), `BuildInfo.g.cs` (stamped by `scripts/stamp-cbot-build.ps1`), deploy via `scripts/deploy-cbot.ps1` |
| **Challenge simulation (FTMO windows)** | `TradingEngine.Risk/Compliance/ChallengeSimulator.cs`, `TradingEngine.Web/Services/ChallengeSimulationService.cs` (`SimulateAsync` + sv2 `ComputeSurvivalAsync`), `GET /api/runs/{id}/challenge-sim` |
| **Commission / swap / net computation** | `TradingEngine.Services/Helpers/TradeCostCalculator.cs` (D9: costs negative, `Net = Gross + Commission + Swap`; per-side notional commission by `CommissionType`) |
| **Config loading** | `TradingEngine.Host/ConfigLoader.cs` (risk/prop-firm/symbols JSON + strategies from DB store) |
| **Config override (per-run)** | `TradingEngine.Services/Helpers/EffectiveConfigResolver.cs` (deep-merge default ← overrides ← run plan); persisted `EffectiveConfigJson` on the run (display caveat F61 for legacy `UsePackId` runs) |
| **Config seeding (JSON→DB)** | `TradingEngine.Infrastructure/Persistence/StrategyConfigSeeder.cs`, `TradingEngine.Web/Configuration/AddOnPackSeeder.cs` |
| **Cross-rate store** | `TradingEngine.Host/CrossRateStore.cs` |
| **cTrader adapter (engine side)** | `TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs` (hello/bar/bar_result/exec handling, per-bar command buffering, `VenueManaged` exits) |
| **cTrader CLI process ownership** | `TradingEngine.Web/Services/CTraderProcessOwner.cs` (dynamic ports, PID-owned, orphan reap), `TradingEngine.CTraderRunner/` (CLI locate + launch) |
| **cTrader desktop capture (listen mode)** | `TradingEngine.Web/Services/CTraderListenService.cs` (fixed ports 15555/15556), `TradingEngine.Web/Api/CtraderListenController.cs` |

### D–L

| Feature | Key Files |
|---------|-----------|
| **Data manager (coverage/sync/gaps)** | `TradingEngine.Web/Api/DataManagerController.cs`, `.../DownloadJobService.cs`, `TradingEngine.Web/Services/AutoSyncService.cs` |
| **DB context / migrations** | `TradingEngine.Infrastructure/Persistence/TradingDbContext.cs`, `.../Persistence/Migrations/` (EF only, no raw SQL) |
| **Determinism** | `PositionId == OrderId` (`PositionLifecycle.CreateIntended`); tests: `DeterminismTests`, `EnginePurityTests` (source scan) |
| **DI wiring** | `TradingEngine.Host/EngineHostFactory.cs` (inner engine host), `TradingEngine.Web/Program.cs` (web host) |
| **Drawdown tracking (kernel)** | `TradingEngine.Engine/DrawdownReducer.cs` (daily/weekly/monthly/max, resets, velocity) |
| **Effects (kernel → world)** | `TradingEngine.Host/EffectExecutor.cs`; feedback in via `TradingEngine.Host/KernelFeedback.cs` |
| **Engine loop (production, all venues)** | `TradingEngine.Host/KernelBacktestLoop.cs` (`RunFromBrokerAsync`, `ProcessBarAsync`), built/run by `EngineRunner.cs` / `EngineWorker.cs` |
| **Engine reducer / state (kernel)** | `TradingEngine.Engine/EngineReducer.cs`, `EngineState` (positions, governor, drawdown, protection, account) |
| **Entry planning (limit orders)** | `TradingEngine.Services/EntryPlanner.cs` (LimitOffset research default D11; SL/TP re-derived off planned entry) |
| **Equity snapshots** | `TradingEngine.Host/KernelEquitySnapshot.cs` → `TradingEngine.Infrastructure/Persistence/EquityPersistenceHandler.cs` → `EquitySnapshots` (+ cache push) |
| **Excursion recording / MAE-MFE** | `TradingEngine.Services/Helpers/ExcursionTracker.cs`; opt-in per-bar excursion paths via `RecordExcursions` custom param (`ReplayVenueRunner`) |
| **Exit lab (offline exit replay)** | `TradingEngine.Services/ExitLab/ExitReplayer.cs`, `TradingEngine.Web/Api/ExitLabController.cs` |
| **Experiments (pre-registered batches)** | `TradingEngine.Web/Api/ExperimentsController.cs`, `TradingEngine.Experiments/` (ExperimentRunner, VariantScorer, WalkForwardSplitter), tables `Experiments` (SpecJson) / `ExperimentRuns` (ScoreJson) |
| **Fill model (both replay venues)** | `TradingEngine.Infrastructure/Adapters/VenueFillModel.cs` (`FirstBreachingTick` — measured rule, F43), `.../SpreadConvention.cs`; contract: `docs/reference/RESTING-ORDER-CONTRACT.md` + `RestingOrderContractTests` |
| **FTMO / prop-firm rules** | `TradingEngine.Domain/RiskAndEquity/PropFirmRuleSet.cs`, `config/prop-firms/*.json`, enforced in-kernel (gate + drawdown + governor toggles) |
| **Governor** | `TradingEngine.Engine/GovernorMachine.cs` (in-kernel), `config/governor.json`, `TradingEngine.Web/Api/GovernorController.cs` |
| **Health / doctor** | `GET /api/system/health` (`SystemController`), `research doctor` (ResearchCli) |
| **Indicators** | `TradingEngine.Infrastructure/Indicators/SkenderIndicatorService.cs`, `TradingEngine.Host/IndicatorSnapshotService.cs` (keyed symbol/tf/type/period — no cross-strategy bleed) |
| **Journal (StepRecords — the only journal)** | `TradingEngine.Engine/Kernel/ChannelJournalWriter.cs` → `TradingEngine.Host/ScopedStepRecordSink.cs` → `TradingEngine.Infrastructure/Persistence/Repositories/SqliteStepRecordSink.cs`; API `GET /api/runs/{id}/journal` + `/journal/export` |
| **Limit/stop resting orders (tape)** | `TradingEngine.Infrastructure/Adapters/TapeReplayAdapter.cs` (`_pendingLimits`/`_pendingStops`, expiry in bars → `ENTRY_EXPIRED`) |
| **Lock-step protocol (cTrader)** | `TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs` + `TradingEngine.Infrastructure/Transport/NetMq/NetMqMessageTransport.cs` (engine), `TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` (cBot); spec in `SYSTEM-REFERENCE.md` §12 |
| **Lot sizing** | `TradingEngine.Engine/Kernel/KernelSizing.cs` (production math + DD scale factor); `LotSizingMethod` enum (5 methods) in Domain |

### M–R

| Feature | Key Files |
|---------|-----------|
| **Market data store (tape source)** | `IMarketDataStore` (Domain interface) + Infrastructure implementation; downloaded canonical history, deduped; managed via Data Manager UI/API + `AutoSyncService` |
| **Monitor (live run view)** | SignalR push from in-memory `BacktestRunState` (`Web/Services/Runs/`), `RunProgressBroadcaster.cs` — zero DB reads during a run |
| **Parity gate (tape vs cTrader)** | `TradingEngine.Web/Services/ParityGateService.cs`, `LedgerReconcileService.cs`, `TradingEngine.Infrastructure/Reconcile/LedgerReconciler.cs`; `research parity` verb; tolerance budget in `iter-alpha-loop/PLAN.md` §P4 |
| **Pass probability (Monte Carlo)** | `TradingEngine.Risk/Compliance/PassProbabilityEstimator.cs`, `TradingEngine.Web/Services/PassProbabilityService.cs` |
| **Pip calculation** | `TradingEngine.Services/PipCalculator.cs` (Distance, PipValuePerLot, GrossPnL) |
| **Position lifecycle (kernel FSM)** | `TradingEngine.Engine/PositionLifecycle.cs` |
| **Pre-trade gate** | `TradingEngine.Engine/Kernel/PreTradeGate.cs` (protection, governor, SL validation, concurrent caps, exposure + `ExposureGroups`, risk budget/heat, worst-case DD projection) |
| **Regime detection** | `TradingEngine.Infrastructure/Indicators/AtrBasedRegimeDetector.cs`, `config/regime.json`, filter options in `TradingEngine.Domain/StrategyBank/RegimeFilterOptions.cs` |
| **Risk profiles** | `TradingEngine.Domain/RiskAndEquity/RiskProfile.cs`, `config/risk-profiles/*.json` (conservative 0.25% / standard 0.5% / aggressive 2% / raw), `RiskProfilesController` |
| **Run cache (UI reads during runs)** | `TradingEngine.Infrastructure/Caching/RunDataCache.cs` (write-through; shared Web ↔ inner host via `EngineHostOptions`), `CacheEvictionSweeper.cs` |
| **Run plan (row-based)** | `TradingEngine.Domain/RunPlan.cs` — `[{StrategyId, Symbol, Timeframe, PackId}]`; `RunPlanBuilder.cs` (Web), `StrategyBankService.GetActive` |
| **Run queries (list/detail/data)** | `TradingEngine.Web/Services/RunQueryService.cs` (facade) → `Services/Runs/` query classes (`RunListQuery`, `RunDetailQuery`, `RunDataQuery`, `RunBarNarrativeQuery`) behind `ILiveRunReader` |
| **Run queue + concurrency** | `BacktestOrchestrator` (bounded tape pool, strictly serial cTrader lane), `RunRegistry` |

### S–W

| Feature | Key Files |
|---------|-----------|
| **Scoring (SetupScore sv2)** | `TradingEngine.Web/Services/SetupScoreService.cs` (composite; survival = `ChallengeSimulationService.ComputeSurvivalAsync`, incomplete = non-pass; validity floor D3 → null-with-reason), `ScoreboardController` |
| **SL/TP calculation** | `TradingEngine.Services/SLTPCalculation/SlTpCalculator.cs` + `SlTpResolver.cs`; exits detected in-kernel (`EngineReducer`/`Kernel.DetectSlTpExit`). **F71:** trend-breakout/rsi-divergence/macd-momentum bypass the resolver for TP — `TakeProfit.Method=None` is a dead knob there |
| **Split-half persistence (F64 test)** | `TradingEngine.Web/Services/SplitHalfPersistenceService.cs`, `GET /api/experiments/persistence`, `research persistence` verb, `tools/research/split_half.py` |
| **Spread / honest fills** | Constant per-run spread via `SpreadConvention`; `HonestFills` custom param (default true) in `ReplayVenueRunner`/`TapeReplayAdapter` |
| **Strategies (9 families)** | `TradingEngine.Strategies/<Family>/<Family>Strategy.cs` + `...Config.cs`; seeds in `config/strategies/*.json`; registry `TradingEngine.Host/StrategyRegistry.cs` |
| **Sweeps** | `TradingEngine.Web/Api/SweepController.cs`, `Services/SweepRunnerService.cs` (tape-only) |
| **Symbol economics (venue-declared)** | cBot `symbol_spec` → `TradingEngine.Infrastructure/SymbolInfoRegistry.cs` (`MergeVenueSpec` — merges everything except spread, F24; in-memory only, F25); fallback `config/symbols.json` (loud warning) |
| **Trade persistence** | `TradingEngine.Infrastructure/Persistence/TradePersistenceHandler.cs` → `TradeResults` (+ cache push); attribution keys `StrategyId + Symbol + EntryTimeframe` on every row |
| **Venue routing (per run)** | `CustomParams["Venue"]`: `tape` / `replay` (default) / `sim` / `ctrader` (explicit opt-in) → `TradingEngine.Web/Services/Venues/IVenueRunner.cs` resolution |
| **Venue sessions (capture history)** | `TradingEngine.Web/Api/VenueSessionsController.cs` |
| **Walk-forward** | `TradingEngine.Web/Api/WalkForwardController.cs`, `Services/WalkForwardBackgroundService.cs`, `TradingEngine.Experiments/WalkForwardSplitter.cs`; OOS ratio (F62) computed in `SetupScoreService` when scoring against a job; carries `PackId`/`RiskProfileId` into folds |
| **Web UI** | Angular SPA in `web-ui/` served single-origin by `TradingEngine.Web` (port 5134); `NgServeHost.cs` for dev; build gotcha: run `npm --prefix web-ui run build` if Angular src is newer than `wwwroot` |

---

## Part 2 — Process Walkthroughs

### 2.1 Backtest end-to-end (UI click → trades in DB)

```
1. SPA posts run request
   web-ui/ → TradingEngine.Web/Api/RunsController.cs → StartRunRequest
     (symbols, TFs, strategy rows, venue, pack/risk, HonestFills, spread, commission)

2. Orchestration
   Web/Services/BacktestOrchestrator.cs      → queue, RunId, lifecycle
   Web/Services/Runs/RunConfigAssembler.cs   → effective config (+EffectiveConfigJson)
   Web/Services/Runs/RunRecordStore.cs       → BacktestRuns row
   Web/Services/Venues/IVenueRunner.cs       → Resolve(venue) → ReplayVenueRunner | CTraderVenueRunner

3. Engine host
   Host/EngineHostFactory.cs                 → inner IHost (adapter factory, RunDataCache handoff)
   Host/EngineRunner.cs                      → warms indicators, starts loop
   Host/KernelBacktestLoop.cs                → per-bar cycle (see SYSTEM-REFERENCE §1)

4. Venue feeds bars
   tape:    Infrastructure/Adapters/TapeReplayAdapter.cs   ← IMarketDataStore
   replay:  Infrastructure/Adapters/BacktestReplayAdapter.cs ← Bars table
   ctrader: Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs ← cBot over NetMQ

5. Decide + execute per bar
   Host/BarEvaluator.cs                      → strategies → OrderProposed
   Engine/Kernel/Kernel.cs                   → PreTradeGate → KernelSizing → effects
   Host/EffectExecutor.cs                    → venue submit / journal / trade close
   Infrastructure/Adapters/VenueFillModel.cs → first-breaching-tick fills (replay venues)
   Services/Helpers/TradeCostCalculator.cs   → gross/commission/swap/net (negative costs)

6. Persist (write-through to RunDataCache)
   Infrastructure/.../SqliteStepRecordSink.cs        → JournalEntries (batched)
   Infrastructure/Persistence/TradePersistenceHandler.cs → TradeResults
   Infrastructure/Persistence/EquityPersistenceHandler.cs → EquitySnapshots

7. Finalize + read
   BacktestOrchestrator (single FinalizeRunAsync)    → terminal status, totals
   Web/Services/RunQueryService.cs → Runs/ queries    → cache-first reads; monitor via SignalR
```

### 2.2 Strategy signal → fill (kernel path)

```
Strategy.Evaluate(MarketContext) → TradeIntent            TradingEngine.Strategies/*
  → EntryPlanner (order type, LimitOffset price, SL/TP re-derivation)
  → BarEvaluator emits OrderProposed
  → Kernel.Decide: PreTradeGate.Evaluate (reject → journal GuardResult)
                   KernelSizing.Calculate (lots, DD scaling)
  → EngineEffect: SubmitOrder → EffectExecutor → IBrokerAdapter.SubmitOrderAsync
  → venue rests/fills per RESTING-ORDER-CONTRACT (limit: first ask/bid touch; fill at
    first breaching O/H/L/C tick; expiry in bars → OrderCancelled)
  → ExecutionEvent back via KernelFeedback → OrderFilled → PositionLifecycle Open
```

### 2.3 Position close → cost → journal

```
Exit detected (in-kernel):
  SL/TP:   EngineReducer / Kernel.DetectSlTpExit per exit-TF bar (venue fills per contract;
           cTrader detects natively — VenueManaged reconcile)
  Flatten: KernelTimeFlattenEvaluator / KernelWeekendFlattenEvaluator / breach effects
  Trailing/BE moved SL: KernelTrailingEvaluator → StopLossModifyRequested

Close execution → TradeCostCalculator:
  gross = PipCalculator.GrossPnL(...)
  commission = per-side notional model by CommissionType (negative)
  swap = nights × venue rate (signed, weekends free, ×3 Wednesday)
  net = gross + commission + swap        ← invariant-tested on every row

TradeResult persisted (TradePersistenceHandler) with StrategyId/Symbol/EntryTimeframe,
R-multiples, MAE/MFE; PartialTp closes write a separate row per partial —
NOTE (F70): row-level ExpectancyR is NOT comparable across partial/non-partial configs;
family evaluation uses position-level dollars.
```

### 2.4 Config: JSON → seed → DB → effective → run

```
config/strategies/*.json  → StrategyConfigSeeder → StrategyConfigs (DB canonical)
config/risk-profiles/*.json + prop-firms/*.json + symbols.json → ConfigLoader (JSON)
AddOnPackSeeder → packs (runner-aggressive, breakeven-only, ...)

Per run: RunConfigAssembler + EffectiveConfigResolver
  deep-merge: stored default ← per-run overrides ← run-plan row (PackId replaces own add-ons;
  StripAddOns → bare SL/TP) → EffectiveConfigJson persisted on the run
Runtime: SymbolInfoRegistry may override symbol economics with venue-declared specs (§5)
```

### 2.5 Research loop (experiment → score → walk-forward → verdict)

```
Pre-register variants (ledger + Experiments.SpecJson)      docs/iterations/<iter>/LEDGER.md
  → runs via RunsController / SweepRunnerService / ResearchCli playbooks (venue=tape, D13 one cell/run)
  → SetupScoreService.ScoreRunAsync (sv2; validity floor → null-with-reason)
  → scoreboard: ScoreboardController / `research scoreboard` → evidence/scoreboard-*.md
  → walk-forward best variant: WalkForwardController (6 folds) → OOS ratio → cull <0.5 → StrategyCellParks
  → split-half: `research persistence --experiment <id> --split <date>`
  → parity (before owner-facing claims): `research parity` → VERDICT
Embargo windows: never create BacktestRuns rows inside an embargo before its sanctioned touch.
```

---

## Part 3 — Test File to Code Mapping

| Test | Validates |
|------|-----------|
| `Unit/.../EngineReducerTests`, `PositionLifecycleTests`, `DrawdownReducerTests` | `TradingEngine.Engine` reducers/FSM |
| `Unit/.../PreTradeGate*`, `KernelSizing*` tests | Gate + sizing math |
| `Unit/.../VenueFillModelTests` | First-breaching-tick fills — pinned to 6 recorded cTrader fills |
| `Unit/.../VenueSwapModelTests` | Swap model — pinned to 3 recorded venue swap charges |
| `Unit/.../TradeCostCalculatorTests` | Cost math + sign convention |
| `Unit/.../ChallengeSimulatorTests` | FTMO window semantics (incl. daily-cap-dominates-target) |
| `Integration/.../SetupScoreSv2Tests` | sv2 survival scoring end-to-end |
| `Integration/.../SplitHalfPersistenceTests` | F64 persistence table (synthetic experiment) |
| `Simulation/.../DeterminismTests` | Byte-identical journal on re-run |
| `Simulation/.../RestingOrderContractTests` | Both venues obey the resting-order contract |
| `Simulation/.../FtmoGoldenJourneyTests` + `golden-snapshot.json` | Kernel behaviour vs golden oracle (D81) |
| `Architecture/.../EnginePurityTests` | No UtcNow/NewGuid/I-O in Engine source; layer boundaries |
| `Simulation` `RequiresCTrader=true` suite | Real cBot under ctrader-cli + ledger reconciliation (see `ctrader-e2e` skill) |

Baseline (2026-07-16): Unit 767/0/6 · Integration 153/0/0 · Simulation-fast 144/0/0.
