# Test Taxonomy

## Unit Tests (`TradingEngine.Tests.Unit`)
Pure logic tests — no DI, no DB, no external services.
- **Engine** — Reducers, FSMs, guard cells (DrawdownReducer, RiskGate, PositionLifecycle, EngineReducer)
- **Services** — OrderDispatcher validation paths
- **Risk** — Governor state machine, sizing modifiers

Run: `dotnet test tests/TradingEngine.Tests.Unit`

## Integration Tests (`TradingEngine.Tests.Integration`)
Event-script in, effect-list + DB rows out.
- **DI Validation** — Service resolution verification
- **Unified Decision Journal** — DecisionRecord persistence round-trip
- **Repositories** — Trade/Bar/EventLog persistence
- **Migrations** — Fresh DB migration succeeds
- **Web Smoke** — HTTP endpoints return 200

Run: `dotnet test tests/TradingEngine.Tests.Integration`

## Simulation Tests (`TradingEngine.Tests.Simulation`)
Full end-to-end pipeline tests.
- **Pipeline** — `CtraderPipelineDiagnosticTest`, `FullBacktestPipelineTest`, `NetMQBridgeTest`
- **Scenarios** — `DrawdownScenarios`, `MultiStrategyScenarios`, `TrendBreakoutScenarios`
- **Risk** — `CurrencyExposureTests`, `AtrRegimeScalingTests`, `WeeklyDDProtectionTests`
- **Characterization** — `PositionLifecycleGoldenTests` (current-behavior snapshots)

Run: `dotnet test tests/TradingEngine.Tests.Simulation` (takes several minutes; 28 tests)

## Architecture Tests (`TradingEngine.Tests.Architecture`)
Boundary invariants enforced via reflection.
- `Engine_references_only_Domain` — Engine assembly purity
- `Engine_has_no_ILogger_no_DateTimeNow` — No infrastructure in Engine
- `EngineMode_only_in_host_and_infrastructure` — Mode confined to composition layer

Run: `dotnet test tests/TradingEngine.Tests.Architecture`
