# Iteration 19 — HANDOVER.md

**Branch**: `dev` (merged from `iter/19-live-ui`)  
**Implemented**: 2026-06-12  
**Status**: Complete — PR1/PR2/PR3/PR4 delivered, 133 tests pass  
**Spans**: `iter/19-governor` → `iter/19-research-lab` → `iter/19-trade-intelligence` → `iter/19-live-ui`

---

## Commits: 2 (consolidated)

| Commit | PR | Content |
|--------|-----|---------|
| `9f5654d` | PR1+PR2+PR3 | Loss enforcement, governor, research harness, trade intelligence |
| `e7f805f` | PR4 | Live UI, daily protection ledger, dashboard rewrite |

---

## Test Results: 133 passing

| Suite | Count | Status |
|-------|-------|--------|
| Unit tests | 116 | All pass |
| Integration tests | 17 | All pass (incl. DI validation + migration) |
| Simulation tests | N/A | 13 pre-existing failures on `dev` branch (not caused by iter-19) |

---

## What Was Built

### PR1 — Loss Enforcement + Governor

| Phase | What | Key Files |
|-------|------|-----------|
| G0.1 | Mark-to-market equity in `BacktestReplayAdapter` — tracks open positions, computes floating PnL, emits truthful `AccountUpdate` per bar. Fixes BUG-06 (flat equity curve). | `BacktestReplayAdapter.cs` |
| G0.2 | Breach watchdog — `EnterProtectionMode` fires on both Daily+Max DD causes. `HandleAccountUpdate` checks DD levels at 0.9× hard limit flattens. `ConsumeForceClosePending` loop in backtest path. Fixes BUG-07. | `RiskManager.cs`, `EngineWorker.cs` |
| G0.3 | Exposure pre-check fix — uses `currentMid` for market orders (not SL), uses `MaxLots` for risk estimate. Fixes BUG-08. | `RiskManager.cs`, `OrderDispatcher.cs` |
| G0.4 | Budget-aware entry guard — new trade risk ≤ 25% of remaining daily budget. Downsize-before-reject logic in `OrderDispatcher`. | `RiskManager.cs`, `OrderDispatcher.cs`, `SizingPolicyOptions.cs` |
| G1 | `TradingGovernorService` — state machine (Normal/Reduced/SoftStop/CoolingOff/ProfitLocked/HardStop). Band, streak, profit-lock rules. All thresholds are fractions of active `PropFirmRuleSet` (P1 compliant). | `TradingGovernorService.cs`, `GovernorTypes.cs`, `GovernorOptions.cs` |
| G2 | Wiring — governor in `EngineHostFactory`, `EngineWorker` hooks (`OnBar`, `OnDailyReset`, `OnWeeklyReset`), governor check in `RiskManager.Validate`, `GovernorSizeModifier` in pipeline, `config/governor.json` + `config/sizing-policy.json`. | `EngineHostFactory.cs`, `EngineWorker.cs`, `ConfigLoader.cs` |

### PR2 — Research Lab

| Phase | What | Key Files |
|-------|------|-----------|
| R1 | `Experiment`/`ExperimentRun` entities, `AddExperiments` migration, `IExperimentRepository` + `SqliteExperimentRepository`. | Persistence entities + migration |
| R2 | `ConfigOverrideApplier` — JSON dot-path overrides on `LoadedConfig`. Unknown paths hard fail. | `ConfigOverrideApplier.cs` |
| R3 | `WalkForwardSplitter` (rolling folds), `ExperimentRunner` (spec validation, per-variant×fold in-process replay via `EngineHostFactory`, tags `BacktestRuns` with `ExperimentRun`). | `WalkForwardSplitter.cs`, `ExperimentRunner.cs` |
| R4 | `VariantScorer` (Monte Carlo pass probability via `PassProbabilityEstimator`, expectancy R, max DD from equity curve, fold consistency). `ExperimentReportWriter` (REPORT.md + report.json). | `VariantScorer.cs`, `ExperimentReportWriter.cs` |
| R5 | CLI: `experiment run|report|list` (in `TradingEngine.Host`). REST: `POST /api/experiments`, `GET /api/experiments[/{id}/report]`. Example spec at `config/experiments/trailing-method-comparison.json`. | `ExperimentCli.cs`, `ExperimentsController.cs` |

### PR3 — Trade Intelligence

| Phase | What | Key Files |
|-------|------|-----------|
| T0 | `PLAYBOOK-AUDIT.md` — per-strategy audit table. `config/playbook.json` — per asset class × timeframe baselines. Fixed `SlTpResolver` `FixedPips` TP bug. Refactored all 9 strategies to use `SlTpResolver` consistently. Added `ISymbolInfoRegistry` to 3 strategies. Updated all 9 config JSONs with `positionManagement` blocks. | All 9 strategy files + configs, `SlTpResolver.cs`, `PLAYBOOK-AUDIT.md`, `playbook.json` |
| T1 | `ReentryOptions` record, `ISignalGate` interface, `SignalGateService` (cooldown per exit reason, bar-aligned not wall-clock). `reentry` blocks added to all 9 strategy JSONs. Wired gate check in engine before order dispatch. | `SignalGateService.cs`, `ReentryOptions.cs`, `ISignalGate.cs`, `EngineWorker.cs` |
| T2 | Extended `TrailingMethod` enum with `Structure`, `SteppedR`. Added `StructureLookbackBars`, `SteppedRLevels` to `TrailingConfig`. `RideOptions`, `PartialTpOptions` records. `StructureTrail` (3-bar fractal swing) + `SteppedRTrail` (R-multiple ratchet) in `TrailingHelpers`. RideOptions gate in `PositionManager`. | `TrailingHelpers.cs`, `PositionManager.cs`, `TrailingMethod.cs`, `PositionManagementOptions.cs` |
| T3 | Partial close protocol — `PositionTracker` dedup fix (partial close keeps position in `_openPositions`). `HandlePartialCloseAsync` + `PositionPartiallyClosed` event. `ClosePartialPositionAsync` added to `IBrokerAdapter` (default → full close). Implemented in `BacktestReplayAdapter`, `SimulatedBrokerAdapter`, `NetMQBrokerAdapter`, `FakeCBot`, `TradingEngineCBot.cs` (net6.0/C#10). `PROTOCOL-DELTA.md` written. | All 6 adapters, `PositionTracker.cs`, `PROTOCOL-DELTA.md` |

### PR4 — Live UI

| Phase | What | Key Files |
|-------|------|-----------|
| G3 | `DailyProtectionLedger` + `ProtectionLedgerEntry` entities, `AddProtectionLedger` migration. `ProtectionLedgerWriter` (subscribes to `GovernorStateChanged`). `GET /api/governor/state`, `GET /api/protection/days`, `GET /api/protection/days/{date}`. | Ledger entities, migration, controllers |
| U1 | `BacktestDashboard.razor` rewrite — 6 panels: run form, governor banner, equity chart, trade feed (color-coded), bar progress (bars/s rate), log panel (50-line tail). 1s polling loop to `/api/backtest/{runId}/status`. | `BacktestDashboard.razor`, `GovernorBanner.razor` |
| U2 | `RunDetail.razor` — summary stats, equity curve chart, signal audit table (per-strategy bars/signals/trades/rejections). Consumes `IBacktestQueryService`. | `RunDetail.razor` |
| U3 | `ExperimentBrowser.razor` — lists experiments, status badges, spec textarea to POST new experiments. | `ExperimentBrowser.razor`, `ExperimentsController.cs` |
| Stubs | Wired `EquityController` to `IBacktestQueryService.GetEquityAsync()`. Wired `EventsController` to `IPipelineEventRepository.GetByRunIdAsync()`. | `EquityController.cs`, `EventsController.cs` |

---

## Architecture Changes

### New Domain Interfaces

| Interface | Location | Purpose |
|-----------|----------|---------|
| `ITradingGovernor` | `Domain/Interfaces/` | Single authority for "should we be trading, at what size" |
| `ISignalGate` | `Domain/Interfaces/` | Re-entry suppression per strategy+symbol+direction |
| `IExperimentRunner` | `Application/Experiments/` | Orchestrates experiment campaigns |
| `IExperimentRepository` | `Infrastructure/Persistence/Repositories/` | Experiment + ExperimentRun CRUD |

### New Domain Records

| Record | File |
|--------|------|
| `GovernorDecision`, `GovernorContext`, `GovernorSnapshot`, `GovernorTradingState` | `RiskAndEquity/GovernorTypes.cs` |
| `GovernorOptions` | `RiskAndEquity/GovernorOptions.cs` |
| `SizingPolicyOptions` | `RiskAndEquity/SizingPolicyOptions.cs` |
| `ReentryOptions` | `Trading/ReentryOptions.cs` |
| `RideOptions`, `PartialTpOptions` | `PositionManagement/PositionManagementOptions.cs` |
| `ExperimentSpec`, `WalkForwardSpec`, `VariantSpec`, `ScoringWeights`, `VariantScore`, `FoldScore` | `Experiments/ExperimentTypes.cs` |

### New Domain Events

| Event | File |
|-------|------|
| `GovernorStateChanged` | `Events/GovernorStateChanged.cs` |
| `PositionPartiallyClosed` | `Events/PositionPartiallyClosed.cs` |

### Config Files Added

| File | Purpose |
|------|---------|
| `config/governor.json` | Governor band/streak/profit-lock thresholds |
| `config/sizing-policy.json` | Flatten fraction, budget use fraction, portfolio heat cap |
| `config/playbook.json` | Per asset class × timeframe entry/exit baselines |
| `config/experiments/trailing-method-comparison.json` | Example experiment spec |

### EF Migrations Added

| Migration | Tables |
|-----------|--------|
| `AddExperiments` | `Experiments`, `ExperimentRuns` |
| `AddProtectionLedger` | `DailyProtectionLedgers`, `ProtectionLedgerEntries` |

---

## Protocol Changes (cBot ↔ Engine)

**`close_partial` command**: Documented in `docs/iterations/iter-19/PROTOCOL-DELTA.md`.
- Engine sends `{type:"close_partial", positionId, lots}` in `bar_done.commands[]`
- cBot calls `ClosePosition(pos, volumeInUnits)` (partial close API)
- cBot returns `{kind:"partial_close", filledLots:partialAmount}` in `bar_result.execs[]`
- Dedup safe: signature includes `FilledLots` which differs from full close

---

## Files Created: 36

| Layer | Files |
|-------|-------|
| Domain | 8 (`GovernorTypes`, `GovernorOptions`, `SizingPolicyOptions`, `ReentryOptions`, `ProtectionCause`, `GovernorStateChanged`, `PositionPartiallyClosed`, `ExperimentTypes`, `ISignalGate`, `ITradingGovernor`) |
| Application | 1 (`IExperimentRunner`) |
| Risk | 2 (`TradingGovernorService`, `GovernorSizeModifier`) |
| Services | 1 (`SignalGateService`) |
| Host | 6 (`ConfigOverrideApplier`, `WalkForwardSplitter`, `ExperimentRunner`, `VariantScorer`, `ExperimentReportWriter`, `ExperimentCli`) |
| Infrastructure | 6 (2 entities, 2 migrations, 2 repositories) |
| Web | 9 (4 controllers, 4 pages, 1 service) |
| Config | 4 (`governor.json`, `sizing-policy.json`, `playbook.json`, experiment spec) |
| Docs | 3 (`PLAN.md`, `PLAYBOOK-AUDIT.md`, `PROTOCOL-DELTA.md`) |

---

## Files Modified: 35+

| Category | Files |
|----------|-------|
| Adapters | 6 (`BacktestReplayAdapter`, `NetMQBrokerAdapter`, `SimulatedBrokerAdapter`, `TradingEngineCBot.cs`, `FakeCBot`, `IBrokerAdapter`) |
| Engine | `EngineWorker.cs`, `EngineHostFactory.cs`, `EngineWorkerDependencies.cs` |
| Risk | `RiskManager.cs` |
| Services | `OrderDispatcher.cs`, `PositionManager.cs`, `PositionTracker.cs`, `SlTpResolver.cs`, `TrailingHelpers.cs`, `ComposedStrategy.cs` |
| Domain | `TrailingMethod.cs`, `TrailingConfig.cs`, `PositionManagementOptions.cs`, `IStrategyConfig.cs`, `IRiskManager.cs` |
| Host | `ConfigLoader.cs`, `Program.cs`, `StrategyRegistry.cs`, `GlobalUsings.cs` |
| Web | `Program.cs`, `GlobalUsings.cs`, `BacktestOrchestrator.cs`, `EquityController.cs`, `EventsController.cs`, `BacktestDashboard.razor` |
| Strategies | All 9 strategies + all 9 configs + all 9 config JSONs |
| Tests | 8 test files (DI validation, RiskManager tests, adapter tests, harnesses) |
| Config | `risk-profiles/standard.json`, `risk-profiles/conservative.json` |

---

## Known Gaps (for Iteration 20)

### Missing Tests (6)

| Test | Priority | Notes |
|------|----------|-------|
| Governor state machine unit tests | High | Bands, precedence, streak, cooling-off, profit lock |
| Causal simulation test (governor ON vs OFF) | High | Scripted losing sequence |
| Partial close dedup regression test | High | Must FAIL on pre-T3 code |
| Monotonic-SL property test | Medium | SL never moves against direction for all trail methods |
| Deterministic E2E experiment test | High | Same spec twice → identical scores |
| No `DateTime.UtcNow` audit | Low | Plan P1 rule |

### Missing Code (4)

| Item | Notes |
|------|-------|
| Playbook inheritance in `ConfigLoader` | `playbook.json` exists but strategies don't inherit defaults from it. Each strategy uses its own JSON values. |
| Typed `BacktestProgressEvent` | Engine still publishes raw string events. Dashboard polls a fragile `/status` endpoint without structured payload. |
| `ProtectionLedgerWriter` DB persistence | Subscribes to `GovernorStateChanged` but only logs — never writes ledger rows. |
| Daily Protection Blazor view | Schema + endpoints exist. No `DayDecisionTimeline.razor` or calendar page built. |

### Missing Docs (3)

| Item | Notes |
|------|-------|
| OPEN-ISSUES.md updates | OBS-01/02/03/05 still marked open |
| 3 evidence experiment reports | `docs/experiments/trailing-method-comparison/REPORT.md` etc. Specs exist, never run |
| WebSmokeTests for new endpoints | `/api/governor/state`, `/api/protection/*`, `/api/experiments` |

### Config Loader

`ConfigLoader.cs` now loads `governor.json`, `sizing-policy.json`, and `playbook.json` (via `Governor`/`SizingPolicy` properties on `LoadedConfig`). `playbook.json` is loaded but **not merged** into strategy defaults — each strategy config has its own `positionManagement` block that takes precedence.

---

## RiskPerTradePercent Changes

| Profile | Old | New |
|---------|-----|-----|
| Standard | 1.0% | 0.5% |
| Conservative | 0.5% | 0.25% |
| Aggressive | 1.0% | 1.0% (unchanged) |

---

## For the Next Agent

1. **Read first**: `docs/iterations/iter-19/PLAN.md` for full architecture. `PLAYBOOK-AUDIT.md` for strategy exit logic. `PROTOCOL-DELTA.md` for protocol changes.
2. **Branch strategy**: Work from `dev`. The `iter/19-*` branches are merged and can be deleted.
3. **Hard rules** (unchanged from iter-17/18):
   - `decimal` for all money/price arithmetic
   - `IEngineClock` everywhere; no `DateTime.UtcNow` in engine code
   - `TradingEngine.Domain` has zero infrastructure dependencies
   - cBot project targets **net6.0 / C# 10** — no C# 11+ constructs
   - Single composition root: `EngineHostFactory`
   - `CancellationToken` on every async method
   - EF migrations only — no raw SQL schema changes
4. **Top priority for iter-20**: Tests (governor state machine, causal simulation, partial close dedup), playbook inheritance, typed progress events.
