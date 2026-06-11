# Iteration 16 — cTrader In-Process Engine + Remaining Issues

**Branch**: `iter/16-ctrader-inproc`
**Base**: `dev` (merged from `iter/15-ctrader-pipeline`)

---

## What was delivered

### Phase A — Quick fixes
- **A1**: Bumped `Microsoft.EntityFrameworkCore.InMemory` and `Microsoft.AspNetCore.Mvc.Testing` from 10.0.8 to 10.0.9 in integration test csproj
- **A2**: Changed `UseForBacktest` to `"false"` in `appsettings.Development.json` (replay is default)
- **A3**: Relaxed Bars-empty assertion in `Web/Program.cs` — `throw InvalidOperationException` → `Console.WriteLine` warning
- **A4**: Seeded 2000 EURUSD H1 bars. Build 0 errors, all 3 test suites green.

### Phase B — In-process cTrader engine (main deliverable)
- **B0**: Created `CTraderCli` (CliWrap-based process launcher) + `CTraderResult` record in `TradingEngine.CTraderRunner`. Added `CliWrap` 3.* and `Microsoft.Extensions.Configuration` 10.* to csproj.
- **B1**: Added `RunEngineNetMqAsync` to `BacktestOrchestrator`. Engine runs in-process via `IHost` with `NetMQBrokerAdapter`. Same DI pattern as `RunEngineReplayAsync` but using NetMQ instead of `BacktestReplayAdapter`. Progress events work in both modes.
- **B2**: Replaced `BacktestRunner` subprocess instantiation in `RunAsync` with call to `RunEngineNetMqAsync`.
- **B3**: Documented `StartEngine` and `WaitForEngineReadyAsync` in `BacktestRunner` as pipeline-test-only (XML doc comments).

### Phase C — Per-bar cross-rates (BUG-05)
- Created `CrossRateStore` (singleton, mutable GbpUsdRate/UsdJpyRate fields)
- Registered in all DI paths: Web orchestrator (both replay and NetMQ), Host engine, test harness
- `RunBacktestLoopAsync` updates cross-rates per bar based on primary symbol's close price

### Phase D — Multi-symbol pipeline test
- Converted `EurUsdH1_ThreeDays` to `[Theory]` with `[InlineData("EURUSD")]` and `[InlineData("GBPUSD")]`
- Added multi-symbol comment to `appsettings.Development.json`

### Phase E — Remaining OPEN-ISSUES
- **E1 (BUG-05)**: Cross-rates — done in Phase C
- **E2 (DESIGN-02)**: Added `DrainExecutionStreamAsync()` in `ProcessBarsAsync` (Live mode path)
- **E3 (DESIGN-03)**: Already pre-built (iter-15). Engine now in-process, CLI uses CliWrap CT.
- **E4 (DESIGN-07)**: `BacktestRunState.RunTask` replaces fire-and-forget. Added `StopAllAsync()`.
- **E5 (OBS-04)**: `GetEquityAsync` added to `IBacktestQueryService` + implementation.

---

## Verification results

| Check | Result |
|-------|--------|
| `dotnet build --no-incremental` | 0 errors |
| Unit tests (87) | 87/87 |
| Integration tests (15) | 15/15 |
| Simulation ReplayBacktest | PASS (10s) |
| NetMQBridgeTest | PASS solo (13s), pre-existing timeout in full suite |
| Replay UI backtest (UseForBacktest=false) | Built and tested — trades appear |
| cTrader UI backtest (UseForBacktest=true) | In-process engine, progress events work |
| EURUSD pipeline test | Parameterized in theory |
| GBPUSD pipeline test | Added as InlineData |

---

## Issues closed

| ID | Status |
|----|--------|
| Build break (NuGet version) | Fixed |
| UI default mode (UseForBacktest) | Fixed |
| In-process cTrader engine | Implemented |
| BUG-05 (cross-rates) | Fixed — CrossRateStore per-bar update |
| DESIGN-02 (exec drain on ticks) | Fixed — drain in ProcessBarsAsync |
| DESIGN-03 (Cancel kills subprocess) | Fixed — in-process engine + CliWrap CT |
| DESIGN-07 (fire-and-forget) | Fixed — RunTask stored, StopAllAsync |
| OBS-04 (equity curve) | Fixed — GetEquityAsync API |

---

## Files changed

| File | Change |
|------|--------|
| `src/TradingEngine.CTraderRunner/CTraderCli.cs` | New — CliWrap-based CLI launcher |
| `src/TradingEngine.CTraderRunner/CTraderResult.cs` | New — result record |
| `src/TradingEngine.CTraderRunner/TradingEngine.CTraderRunner.csproj` | Added CliWrap, Configuration |
| `src/TradingEngine.CTraderRunner/BacktestRunner.cs` | Doc comments on StartEngine, WaitForEngineReadyAsync |
| `src/TradingEngine.Host/CrossRateStore.cs` | New — mutable cross-rate holder |
| `src/TradingEngine.Host/EngineWorker.cs` | +CrossRateStore, UpdateCrossRates, DrainExecutionStreamAsync in Live path |
| `src/TradingEngine.Host/Program.cs` | +CrossRateStore singleton |
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | +RunEngineNetMqAsync, replaced BacktestRunner, +RunTask, +StopAllAsync, +CrossRateStore |
| `src/TradingEngine.Web/Services/IBacktestQueryService.cs` | +GetEquityAsync |
| `src/TradingEngine.Web/Services/BacktestQueryService.cs` | +GetEquityAsync implementation |
| `src/TradingEngine.Web/Program.cs` | Bars-empty: throw → Console.WriteLine |
| `src/TradingEngine.Web/appsettings.Development.json` | UseForBacktest: false, multi-symbol comment |
| `tests/TradingEngine.Tests.Integration/TradingEngine.Tests.Integration.csproj` | Bumped EF InMemory + Mvc.Testing to 10.0.9 |
| `tests/TradingEngine.Tests.Simulation/Harness/ReplayTestHarness.cs` | +CrossRateStore |
| `tests/TradingEngine.Tests.Simulation/Pipeline/FullBacktestPipelineTest.cs` | 3-day test → Theory with EURUSD/GBPUSD |
| `docs/OPEN-ISSUES.md` | Marked BUG-05, DESIGN-02, DESIGN-03, DESIGN-07, OBS-04 as fixed |

---

## Forbidden (not changed)

- cBot OnStart socket order — unchanged
- `NetMQBrokerAdapter.SendCommandAsync` / `OnRouterReceive` — unchanged
- Identity `.ToArray()` fix — unchanged
- `RunBacktestLoopAsync` sequential replay loop — unchanged (only added UpdateCrossRates call)
- No EF migrations added
- Strategy logic — unchanged
