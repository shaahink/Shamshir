# Iteration 10 — Refactoring Plan

> Date: 2026-06-08
> Branch: `phase/8b-bar-tracing`
> Purpose: Record all code smells, design issues, and test gaps found during deep analysis. Phases 1-7 have been completed; phases 8-15 remain as future work.

---

## Completed (This Session)

### Phase 1 — Critical Correctness Fixes ✅

| # | Issue | File | Fix |
|---|---|---|---|
| 1 | ExecutionWriter never completed — backtest shutdown hangs | `DataFeedService.cs:36-42` | Added `sim.ExecutionWriter.Complete()` |
| 2 | Entry price mismatch — Validate used `equity.Equity` ($100k) as price | `RiskManager.cs:65` | Changed fallback from `equity.Equity` → `intent.StopLoss`, added zero-equity guard |
| 3 | DailyResetService passed `0` for equity on startup/ticks | `DailyResetService.cs:17-18,30` | Reads `riskManager.InitialBalance` instead. Added `InitialBalance` to `IRiskManager` and `RiskManager` |
| 4 | EquitySnapshot PeakEquity/DailyStartEquity set to current equity | `EngineWorker.cs:300-302` | Uses `_drawdownTracker.PeakEquity` and `_drawdownTracker.DailyStartEquity` |

### Phase 2 — Web Portal Fixes ✅

| # | Issue | File | Fix |
|---|---|---|---|
| 1 | Web backtest: missing CTrader credentials → silent failure | `BacktestRunner.cs:20-30` | Early validation returns clear `ErrorMessage` when credentials missing |
| 2 | Web backtest: zero trades/profit shown (report.json never parsed) | `BacktestRunner.cs:81-98, 218-250` | Added `TryParseReport()` — reads ctrader-cli report.json, populates NetProfit/MaxDrawdown/TotalTrades/WinningTrades/WinRate |
| 3 | Web Run page: no indication credentials are missing | `Run.cshtml`, `Run.cshtml.cs` | Added `CredentialsConfigured` check and warning banner with `dotnet user-secrets` instructions |
| 4 | Web Index page: shows "-" for symbol/period | `Index.cshtml`, `BacktestOrchestrator.cs` | Added `Symbol`/`Period` fields to `BacktestRunState`, display in table |
| 5 | Web Progress page: auto-reloads on error (bad UX) | `Progress.cshtml` | Replaced auto-reload with graceful close; shows error box for failed runs |
| 6 | Web appsettings.json: no CTrader config guidance | `appsettings.json` | Added CTrader section with comments |

### Phase 3 — Test Fixes ✅

| # | Issue | File | Fix |
|---|---|---|---|
| 1 | NetMQBridgeTest used `BAR_DEBUG` (renamed to `BAR_EVAL` in Iteration 8) | `NetMQBridgeTest.cs:105` | Changed assertion pattern |
| 2 | NetMQBridgeTest used hardcoded ports 15555/15556 | `NetMQBridgeTest.cs:14-15` | Uses `PortHelper.AllocatePair()` |
| 3 | PipeConnectivityTest tested deleted NamedPipeBrokerAdapter | `PipeConnectivityTest.cs` | Deleted |
| 4 | SymbolString param naming for ctrader-cli | `BacktestRunner.cs:171` | Changed `--Symbols` → `--SymbolString` (CLI uses C# property names) |

---

## Remaining (Future Work)

### Phase 4 — Eliminate Type-Check Dispatch Anti-Pattern

8 occurrences of `is SimulatedBrokerAdapter` / `is NetMQBrokerAdapter` across `EngineWorker.cs` and `DataFeedService.cs`.

| # | File:Line | Current | Fix |
|---|---|---|---|
| 1 | `EngineWorker.cs:73` | `_broker is SimulatedBrokerAdapter ? Backtest : Live` | Inject `EngineMode` via constructor |
| 2 | `EngineWorker.cs:95` | `_broker is NetMQBrokerAdapter mq` | Add `OnConnected` to `IBrokerAdapter` interface |
| 3 | `EngineWorker.cs:161` | `_broker is SimulatedBrokerAdapter sim` (hot path) | Add `OnTickReceived(Tick)` to `IBrokerAdapter` with no-op default |
| 4-6 | `DataFeedService.cs:36,49,58` | 3x `broker is SimulatedBrokerAdapter sim` | Replace with `IBrokerAdapter` channel writer properties |

**Proposed IBrokerAdapter additions:**
```csharp
Action? OnConnected { get; set; }
void OnTickReceived(Tick tick) { }
ChannelWriter<Tick>? TickWriter { get; }
ChannelWriter<Bar>? BarWriter { get; }
ChannelWriter<AccountUpdate>? AccountWriter { get; }
ChannelWriter<ExecutionEvent>? ExecutionWriter { get; }
```

### Phase 5 — Data Flow Fixes

| # | Issue | File:Line | Fix |
|---|---|---|---|
| 1 | Fire-and-forget persistence silently loses data | `EngineWorker.cs:305`, `PositionTracker.cs:116` | Add bounded retry queue; graceful shutdown awaits pending saves |
| 2 | Tick/Bar/Account channels silently drop data (DropOldest) | `SimulatedBrokerAdapter.cs:7-12`, `NetMQBrokerAdapter.cs:15-17` | Log rate-limited warning on drop; add channel drop counters |
| 3 | Duplicate SL distance/pip value calculation | `RiskManager.cs:73-76`, `OrderDispatcher.cs:29-31` | Extract `CalculateRiskMetrics()` shared method |
| 4 | 1-tick execution lag in backtest | `EngineWorker.cs:140-162` | Re-drain `_executionEventChannel` after `OnTickReceived` |
| 5 | NetMQ SubmitOrderAsync fire-and-forget | `NetMQBrokerAdapter.cs:167-181` | Add optional request-response timeout pattern |
| 6 | `_bars` has redundant ConcurrentDictionary + lock | `EngineWorker.cs:185` | Remove lock since ProcessBarsAsync is single-threaded per iteration |

### Phase 6 — Remove Dead Code

| # | File | What | Action |
|---|---|---|---|
| 1 | `SimulatedBrokerAdapter.cs:224-228` | `ResolvePipSize()` | Delete — never called |
| 2 | `IMarketDataProvider.cs:7` | `SeekAsync()` | Delete from interface + both implementations |
| 3 | `IStrategy.cs:10` | `PositionBehaviors` | Delete from interface + all 5 strategies (never consumed) |
| 4 | `Program.cs:95-96` | `SessionFilter`, `NewsFilter` DI | Delete registrations (never injected) |
| 5 | `Program.cs:36-37` | `slipPips` dead ternary | Collapse to single 0.5 value |
| 6 | `Program.cs:104-105` | Double registration RiskManager/IRiskManager | Merge into single `AddSingleton<IRiskManager, RiskManager>()` |

### Phase 7 — Eliminate Hardcoded Values

| # | Value | Locations | Fix |
|---|---|---|---|
| 1 | `"standard"` magic string | 6 locations | Add `RiskProfile.DefaultId` constant |
| 2 | Cross-rates 149.50, 1.2650 | `Program.cs:99-100` | Move to `config/symbols/cross-rates.json` |
| 3 | `GetBarDuration()` duplicated | `EngineWorker.cs:366`, `HistoricalDataProvider.cs:112` | Move to shared `TradingEngine.Services` utility |
| 4 | Hardcoded EURUSD SymbolInfo | `ComposedStrategy.cs:52`, `TrailingStopService.cs:11`, `SlTpCalculator.cs:16,39` | Inject `ISymbolInfoRegistry` |
| 5 | Channel capacities (10k/2k/1k) duplicated | `SimulatedBrokerAdapter`, `NetMQBrokerAdapter` | Shared `ChannelConfig` record |
| 6 | `..` path navigation | 7 locations in `Program.cs`, `BacktestRunner.cs` | `SolutionPathResolver` utility |

### Phase 8 — Test Quality Improvements

| # | Issue | Fix |
|---|---|---|
| 1 | 4 of 5 strategies have zero tests | Write unit tests for SessionBreakout, MeanReversion, EmaAlignment, ComposedStrategy |
| 2 | NetMQBrokerAdapter has zero unit tests | Tests for DispatchMessage, malformed JSON, channel writes, command sending |
| 3 | EngineTestHarness bypasses entire engine pipeline | Add `EnginePipelineTest` using real EngineWorker with channels |
| 4 | `Reset_ClearsInternalState` is a no-op test | Fix or delete `TrendBreakoutStrategyTests.cs:75-87` |
| 5 | Weak assertions (count>0 instead of specific values) | Strengthen assertions across scenario tests |
| 6 | SymbolInfoRegistry duplicated in 13+ test files | Create `TestFixtures` static class |
| 7 | Flaky timing tests (NetMQBridgeTest has 10+ Task.Delay calls) | Replace with signal-based waits |
| 8 | ThreeDays test tagged Fast but requires CTrader credentials | Retag as Slow or add skip guard |
| 9 | CSV test data not committed to git | Commit generated files or call `GenerateAll()` before tests |

### Phase 9 — Design Improvements

| # | Issue | Fix |
|---|---|---|
| 1 | EngineMode enum underutilized — mode check via type casts | Single dispatch via `_engineMode` set at construction |
| 2 | SimulatedBrokerAdapter PnL bypasses PipCalculator.GrossPnL | Use canonical `PipCalculator.GrossPnL(position, exitPrice, symbolInfo)` |
| 3 | EquityPersistenceHandler 5s fixed flush; batch=100, capacity=10000 | Use batch-or-time pattern (100 items or 5s); increase batch to 500 |
| 4 | TypedEventBus no error isolation between handlers | Wrap each handler in try/catch |
| 5 | `IStrategy.Stats` never consumed | Wire Performance page to display strategy stats |
| 6 | `_reusableIndicatorDict` shared mutable state | Build per-evaluation snapshot instead of mutating shared dict |
| 7 | ResetState races with ProcessBarsAsync on `_bars.Clear()` | Route reset through a serialized action channel |

---

## Dependency Order

```
Phase 4 (Type-Check Dispatch) ── enables cleaner data flow
    │
    ├── Phase 5 (Data Flow Fixes) ── depends on Phase 4 interface additions
    │
    ├── Phase 6 (Dead Code) ── independent, safe cleanup
    │
    ├── Phase 7 (Hardcoded Values) ── independent, safe cleanup
    │
    ├── Phase 8 (Tests) ── can run in parallel with 4-7
    │
    └── Phase 9 (Design) ── final polish, depends on all above
```

## Decay Tracking

Counts of each issue class found:

| Class | Instances | Fixed |
|---|---|---|
| Type-check dispatch (`is XxxAdapter`) | 8 | 0 |
| Mode detection branches | 6 | 0 |
| Dead code (methods/interfaces/registrations) | 6 | 0 |
| Hardcoded values | 6 categories | 0 |
| Hardcoded strings (`"standard"`, cross-rates) | 8 | 0 |
| Duplicated code (GetBarDuration, SL calculations) | 3 | 0 |
| Empty catch blocks | 4 | 0 |
| Stub API controllers | 5 | 0 |
| Interface members never consumed | 3 | 0 |
| Strategies with zero tests | 4 | 0 |
