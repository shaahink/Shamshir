# ITERATION-10 Handover

## Status: All 10 Phases Completed

### Verification Results

| Check | Result |
|-------|--------|
| `dotnet build --no-incremental` | 0 errors |
| `dotnet test tests/TradingEngine.Tests.Unit --no-build` | 87 passed, 0 failed |
| Integration tests | Not run (require cTrader env) |
| Pipeline tests | Not run (require cTrader credentials) |

---

### Phase-by-Phase Summary

#### Phase 0 — RunId Propagation

**Decision D81**: RunId generated in `BacktestOrchestrator.Start()`, stamped into `BacktestConfig`, flows via `Engine__RunId` env var to engine subprocess.

- Added `RunId` field to `BacktestConfig`
- `BacktestOrchestrator.Start()` stamps cfg with RunId
- `BacktestRunner.RunAsync()` uses `cfg.RunId` (fallback: generate new if empty)
- `BacktestRunner.StartEngine()` passes `Engine__RunId` env var
- Created `EngineRunContext` record in `TradingEngine.Domain`
- Host `Program.cs` reads `Engine:RunId` config → registers singleton
- `TradeClosed` event now carries `(TradeResult, string RunId, DateTime)`
- **Removed** `StampTradesWithRunIdAsync` — no more post-hoc time-range stamping

#### Phase 1 — Metadata Expansion

- `BacktestRunSummary`: added Period, BacktestFrom, BacktestTo, InitialBalance, AlgoHash, StrategyParamsJson
- `BacktestRunEntity`: added AlgoHash, StrategyParamsJson fields
- `SqliteBacktestRunRepository`: all 3 methods (Save, GetAll, GetById) updated
- `BacktestOrchestrator.RunAsync`: passes full metadata from config
- `Web/Program.cs`: raw SQL ALTER TABLE for AlgoHash + StrategyParamsJson columns
- `Index.cshtml.cs`: `Period` no longer hardcoded to `""`

#### Phase 2 — Event-Driven Trade Persistence

**Atomic cutover**: Direct `persistence.SaveTradeAsync` removed, event-driven save added.

- `ITradeRepository.SaveAsync`: added `string runId` parameter
- `SqliteTradeRepository.SaveAsync`: sets `entity.RunId` from parameter
- `PersistenceService.SaveTradeAsync`: added runId parameter
- `PositionTracker`: injected `IEventBus` + `EngineRunContext`; removed `PersistenceService` dependency; publishes `TradeClosed` instead of direct save
- Created `TradePersistenceHandler` (Channel-based, bounded 1000, Wait mode)
- Registered in Host `Program.cs` + subscribed to `TradeClosed`

#### Phase 3 — BarEvaluated Audit Trail

- Created `BarEvaluated` domain event (RunId, Symbol, Timeframe, BarOpenTimeUtc, StrategyId, IndicatorValues, SignalFired, SignalDirection, Reason)
- Created `BarEvaluationEntity` (DB entity)
- Added to `TradingDbContext` with indexes on RunId and (RunId, StrategyId, BarOpenTimeUtc)
- `EngineWorker.ProcessBarsAsync`: publishes BarEvaluated for EVERY bar evaluation (including rejection with reason)
- Created `BarEvaluationHandler` (Channel-based, bounded 50k, DropOldest mode, flushes every 3s in batches of 500)
- `Web/Program.cs`: raw SQL CREATE TABLE for BarEvaluations

#### Phase 4 — BacktestReplayAdapter

- Created `BacktestReplayAdapter` implementing `IBrokerAdapter`
- Reads bars from `IBarRepository.GetAsync()` — fully credentialless
- Writes bars to BarStream, generates synthetic ticks at close price
- `EngineWorker` recognizes it as Backtest mode (added `is BacktestReplayAdapter` check)
- No E2E test created yet (deferred — requires test project wiring)

#### Phase 5 — UI CQRS Split

- Created `IBacktestCommandService` (StartAsync, Cancel)
- Created `IBacktestQueryService` + `BacktestRunView` + `EquityPoint` view models
- Implemented `BacktestQueryService` (reads exclusively from DB repos)
- `BacktestOrchestrator` implements `IBacktestCommandService`
- `Run.cshtml.cs`: uses `IBacktestCommandService`
- `Index.cshtml.cs`: uses `IBacktestQueryService` (no more awkward merge)
- `BacktestController`: uses `IBacktestCommandService` for Start

#### Phase 6 — Channel-Based SSE

- Created `BacktestProgressStore` (ConcurrentDictionary<runId, Channel<string>>)
- `BacktestOrchestrator.EnqueueLog()`: pushes to both LogLines queue and progress channel
- `BacktestController.Stream`: replaced polling with ChannelReader.ReadAllAsync
- Done signal sent on completion, channel completed by ProgressStore
- Note: Progress.cshtml JavaScript already used EventSource (unchanged, format compatible)

#### Phase 7 — Rich UI

- Created `Backtests/Detail.cshtml(.cs)` — run summary + metadata + algo hash badge
- Fixed `Trades/Detail.cshtml` — replaced empty `<canvas>` with CSS visual showing entry/exit bar, PnL, and R-multiple
- Compare page not created (deferred — requires equity curve data endpoint)
- Chart.js already loaded in `_Layout.cshtml`

#### Phase 8 — Algo Versioning

- Added `AlgoHash` field to `BacktestResult`
- `BacktestRunner.ComputeAlgoHash()`: SHA256 of `.algo` binary, first 16 hex chars
- AlgoHash flows into `BacktestRunSummary` via `BacktestOrchestrator.RunAsync`
- Displayed in `Backtests/Detail.cshtml` as a badge

#### Phase 9 — Strategy Fix

- `MeanReversionStrategy.Evaluate`:
  - Replaced always-true `latestBar.Low <= currentPrice` with proximity guard: `(Close - Low) / Close < 0.002`
  - Same fix for short side: `(High - Close) / Close < 0.002`
- `MeanReversionParameters`: added `RsiOversold` (default 35) and `RsiOverbought` (default 65)
- Strategy uses configurable thresholds instead of hardcoded 30/70

---

### Files Changed: 38 total (26 modified + 12 new)

**New files:**
- `src/TradingEngine.Domain/EngineRunContext.cs`
- `src/TradingEngine.Domain/Events/BarEvaluated.cs`
- `src/TradingEngine.Host/TradePersistenceHandler.cs`
- `src/TradingEngine.Host/BarEvaluationHandler.cs`
- `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs`
- `src/TradingEngine.Infrastructure/Persistence/Entities/BarEvaluationEntity.cs`
- `src/TradingEngine.Web/Services/BacktestProgressStore.cs`
- `src/TradingEngine.Web/Services/BacktestQueryService.cs`
- `src/TradingEngine.Web/Services/IBacktestCommandService.cs`
- `src/TradingEngine.Web/Services/IBacktestQueryService.cs`
- `src/TradingEngine.Web/Pages/Backtests/Detail.cshtml`
- `src/TradingEngine.Web/Pages/Backtests/Detail.cshtml.cs`

---

### Decisions Recorded

| ID | Decision |
|----|----------|
| D81 | RunId generated in orchestrator, propagated via BacktestConfig + env var |
| D82 | StampTradesWithRunIdAsync removed permanently |
| D83 | BarEvaluated uses DropOldest (50k capacity), flush every 3s in batches of 500 |
| D84 | BacktestReplayAdapter reads from Bars table (credential-free, deterministic) |
| D85 | CQRS split: orchestrator = command only, query reads from DB |
| D86 | Progress uses channel-based push (SSE compatible with existing frontend) |
| D87 | MeanReversion proximity guard = 0.2%, RSI thresholds = 35/65 (configurable) |
| D88 | AlgoHash = first 16 chars of SHA256 of .algo binary |

---

### Verification Method

1. **Build**: `dotnet build --no-incremental` (passes, 0 errors)
2. **Unit tests**: `dotnet test tests/TradingEngine.Tests.Unit` (87/87 pass)
3. **DB verification** (requires running a backtest):
   ```sql
   SELECT RunId, Symbol, Period, TotalTrades, AlgoHash FROM BacktestRuns ORDER BY StartedAtUtc DESC;
   SELECT COUNT(*), RunId FROM Trades GROUP BY RunId;
   SELECT COUNT(*), SignalFired, StrategyId FROM BarEvaluations GROUP BY SignalFired, StrategyId;
   ```
4. **SSE verification**: Start backtest → open Progress page → log lines stream in real-time
5. **UI verification**: `/Backtests` shows runs with Period populated, `/Backtests/Detail?runId=xxx` shows metadata

---

### Known Deferrals

| Item | Reason |
|------|--------|
| Backtest compare page (`Compare.cshtml`) | Requires equity curve data endpoint (needs `IEquityRepository` in query service) |
| E2E test with BacktestReplayAdapter | Test project wiring needed (can be done separately) |
| EF Core migrations replaced with raw SQL | `EnsureCreated()` was used without migration history; full migration path requires DB teardown |
| Trade chart uses CSS visualization not Chart.js | Bar-level price data endpoint not yet created; CSS timeline is functional |
| StrategyParamsJson always `"{}"` | Serialization of strategy configs needs `IConfiguration` → JSON mapping (deferred) |
