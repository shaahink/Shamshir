# Code Map — Feature & Process to File Reference

**For**: Any agent needing to find "where is X implemented" without grepping the codebase.
**Updated**: 2026-07-10 (iter/parity-pipeline — docs/cleanup-refs)

> ## ⚠ iter-36 UPDATE (2026-06-20) — kernel cutover; some entries below are pre-cutover
> - **Engine (production):** `Host/EngineRunner` → `Host/KernelBacktestLoop.RunFromBrokerAsync` →
>   `Host/BarEvaluator` (signals→`OrderProposed`) → `Engine/Kernel/Kernel` (`PreTradeGate`+`KernelSizing`) →
>   `Host/EffectExecutor` → venue + `Host/KernelFeedback`. Trailing: `Host/KernelTrailingEvaluator` →
>   `StopLossModifyRequested` (reducer). Equity snapshot: `Host/KernelEquitySnapshot`.
> - **Imperative twins are TEST-ONLY now:** `OrderDispatcher`/`KernelOrderGate`/`AccountProcessor` live in
>   `tests/TradingEngine.Tests.Support` (golden oracle, D81), NOT `src`. `TradingLoop`/`PositionTracker`
>   remain in `src` (oracle shell + tracker) but are not in the production engine path.
> - **Journal:** single lossless StepRecord stream — `Engine/Kernel/ChannelJournalWriter` →
>   `Host/ScopedStepRecordSink` → `Infrastructure/.../SqliteStepRecordSink` (`JournalEntries`). Query:
>   `Infrastructure/.../SqliteJournalQueryRepository`; API: `Web/Api/RunsController` `/journal` + `/journal/export`.
>   `PipelineEventWriter`/`BarEvaluationHandler` are **deleted** (D83); `NullDecisionJournal`/`NullPipelineJournal`
>   (`Infrastructure/NullJournals.cs`) bind legacy consumers.
> - **Duplicate/identity:** `Web/Api/RunsController` `POST /api/runs/{id}/duplicate`; identity hashing
>   `Infrastructure/ConfigSetHash`; `ParentRunId`/`DatasetId`/`ConfigSetId` on `BacktestRunEntity`.
>
> See `docs/iterations/iter-36/HANDOVER.md` (ROUND 2) + `DECISIONS.md` D81–D84.

---

## Part 1 — Feature Index

Find a feature → see which files implement it.

### A–C

| Feature | Key Files |
|---------|-----------|
| **Account state sync** | `TradingEngine.Host/EngineWorker.cs` (live path), `TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs` |
| **API endpoints** | `TradingEngine.Web/Api/BacktestController.cs`, `...Web/Api/*Controller.cs` |
| **Backtest orchestration** | `TradingEngine.Web/Services/BacktestOrchestrator.cs`, `...Web/Services/BacktestProgressStore.cs`, `...Web/Pages/Backtests/Run.cshtml(.cs)` |
| **BacktestReplayAdapter (venue)** | `TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` |
| **Bar accumulation** | `TradingEngine.Host/EngineWorker.cs` (live), `TradingEngine.Host/TradingLoop.cs` |
| **Bar evaluation persistence** | `TradingEngine.Host/BarEvaluationHandler.cs`, `TradingEngine.Infrastructure/Persistence/TradingDbContext.cs` |
| **Bar synthesis (CSV)** | `tests/TradingEngine.Tests.Simulation/` (`BarBuilder.cs`, `CsvDataGenerator`), `TradingEngine.Infrastructure/HistoricalDataProvider.cs` |
| **Breakeven** | `TradingEngine.Services/PositionTracker.cs`, `TradingEngine.Domain/PositionManagement/` |
| **Breach watchdog** | `TradingEngine.Risk/RiskManager.cs` (`EnterProtectionMode`), `TradingEngine.Host/AccountProcessor.cs` |
| **Budget downsizing** | `TradingEngine.Risk/RiskManager.cs` (worst-case projection), `...Risk/SizeModifierPipeline.cs` |
| **cBot (cTrader)** | `TradingEngine.Adapters.CTrader/TradingEngineCBot.cs`, `.../PipeClient.cs`, `.../OrderCommandHandler.cs` |
| **Commission (computation)** | `TradingEngine.Services/Helpers/TradeCostCalculator.cs` |
| **Config loading** | `TradingEngine.Host/ConfigLoader.cs`, `TradingEngine.Infrastructure/Persistence/StrategyConfigSeeder.cs` |
| **Config override (per-run)** | `TradingEngine.Services/Helpers/EffectiveConfigResolver.cs` |
| **Config seeding (JSON→DB)** | `TradingEngine.Infrastructure/Persistence/StrategyConfigSeeder.cs` |
| **Config store (DB)** | `TradingEngine.Infrastructure/Persistence/SqliteStrategyConfigStore.cs`, `.../IStrategyConfigStore.cs` |
| **Cross-rate store** | `TradingEngine.Host/CrossRateStore.cs` |
| **cTrader adapter (engine side)** | `TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs` |
| **cTrader CLI runner** | `TradingEngine.CTraderRunner/BacktestRunner.cs`, `.../CTraderCliLocator.cs` |
| **Currency exposure** | `TradingEngine.Risk/CurrencyExposureTracker.cs` |

### D–I

| Feature | Key Files |
|---------|-----------|
| **Daily reset** | `TradingEngine.Host/DailyResetService.cs` (scheduler), `TradingEngine.Risk/DrawdownTracker.cs` (math), `TradingEngine.Risk/PropFirmRuleSet.cs` (config) |
| **Data feed (backtest)** | `TradingEngine.Host/DataFeedService.cs`, `TradingEngine.Infrastructure/HistoricalDataProvider.cs` |
| **DB context (EF Core)** | `TradingEngine.Infrastructure/Persistence/TradingDbContext.cs`, `.../ReportingDbContext.cs` |
| **DB migrations** | `TradingEngine.Infrastructure/Persistence/Migrations/` |
| **DI wiring** | `TradingEngine.Host/EngineHostFactory.cs`, `.../EngineServiceCollectionExtensions.cs`, `.../Program.cs` |
| **Domain types (value objects)** | `TradingEngine.Domain/` — all files; core: `Trading/TradeIntent.cs`, `Trading/Position.cs`, `Trading/TradeResult.cs`, `RiskAndEquity/RiskProfile.cs`, `RiskAndEquity/PropFirmRuleSet.cs`, `SymbolInfo/SymbolInfo.cs`, `MarketData/MarketContext.cs` |
| **Drawdown scaling (lot)** | `TradingEngine.Risk/DrawdownScaler.cs` |
| **Drawdown tracking** | `TradingEngine.Risk/DrawdownTracker.cs` (peak, daily, max), `TradingEngine.Domain/Engine/DrawdownReducer.cs` (kernel — half-wired) |
| **EF entity mappings** | `TradingEngine.Infrastructure/Persistence/Mappings/` |
| **Engine clock** | `TradingEngine.Domain/EngineClock.cs` (IEngineClock) |
| **Engine loop (main)** | `TradingEngine.Host/EngineWorker.cs`, `TradingEngine.Host/TradingLoop.cs` |
| **Engine reducer (kernel)** | `TradingEngine.Domain/Engine/EngineReducer.cs` |
| **Engine state (kernel)** | `TradingEngine.Domain/Engine/EngineState.cs` |
| **Entry planner (limit orders)** | `TradingEngine.Services/Helpers/EntryPlanner.cs` |
| **Entry type (order entry config)** | `TradingEngine.Domain/OrderEntryOptions.cs` |
| **Equity persistence** | `TradingEngine.Host/EquityPersistenceHandler.cs`, `TradingEngine.Infrastructure/Persistence/Repositories/SqliteEquityRepository.cs` |
| **Equity snapshot processing** | `TradingEngine.Host/AccountProcessor.cs` |
| **Event bus** | `TradingEngine.Infrastructure/Events/TypedEventBus.cs` |
| **Exception (no signals)** | All strategies have try/catch in `Evaluate()` |
| **Execution dedup** | `TradingEngine.Services/PositionTracker.cs` (\_recentExecOrder LRU) |
| **Execution event handling** | `TradingEngine.Services/PositionTracker.cs` (`OnExecutionAsync`), `TradingEngine.Host/EffectExecutor.cs` |
| **Exit reason determination** | `TradingEngine.Services/PositionTracker.cs` (`DetermineExitReason`), `TradingEngine.Domain/Trading/ExitReason.cs` |
| **Exposure tracking** | `TradingEngine.Risk/CurrencyExposureTracker.cs` |
| **FTMO rules** | `TradingEngine.Risk/PropFirmRuleValidator.cs`, `TradingEngine.Risk/RiskManager.cs` (`Validate`), `config/prop-firms/ftmo-standard.json` |
| **Governor (session)** | `TradingEngine.Risk/TradingGovernorService.cs`, `TradingEngine.Domain/Engine/GovernorMachine.cs` (kernel — half-wired) |
| **Indicator cache keys** | `TradingEngine.Infrastructure/Indicators/IndicatorCache.cs` |
| **Indicator computation** | `TradingEngine.Infrastructure/Indicators/SkenderIndicatorService.cs`, `TradingEngine.Host/IndicatorSnapshotService.cs` |
| **Interfaces (domain)** | `TradingEngine.Domain/Interfaces/` — all; core: `IBrokerAdapter.cs`, `IStrategy.cs`, `IRiskManager.cs`, `IPipelineJournal.cs`, `ISymbolInfoRegistry.cs`, `IIndicatorService.cs` |

### J–O

| Feature | Key Files |
|---------|-----------|
| **Journal (API)** | `TradingEngine.Web/Api/BacktestController.cs` (`/api/backtest/{runId}/journal`) |
| **Journal (normalization)** | `TradingEngine.Services/Helpers/JournalNormalizer.cs` |
| **Journal (persistence)** | `TradingEngine.Host/PipelineEventWriter.cs`, `TradingEngine.Infrastructure/Persistence/Repositories/SqlitePipelineEventRepository.cs` |
| **Journal (UI viewer)** | `TradingEngine.Web/Pages/Backtests/Report.cshtml(.cs)` |
| **Limit orders (rest/expire)** | `TradingEngine.Services/Helpers/EntryPlanner.cs` (plan), `TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` (replay venue), `TradingEngine.Infrastructure/Adapters/SimulatedBrokerAdapter.cs` (synthetic venue) |
| **Live monitor (SSE/SignalR)** | `TradingEngine.Web/Pages/Monitor.cshtml(.cs)`, `TradingEngine.Web/Services/BacktestProgressStore.cs` |
| **Lock-step protocol (cTrader)** | `TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs` (engine side), `TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` (cBot side) |
| **Lot sizing** | `TradingEngine.Risk/PositionSizer.cs` (5 methods), `TradingEngine.Risk/RiskManager.cs` (`CalculateLotSize`) |
| **MAE/MFE (excursion)** | `TradingEngine.Services/ExcursionTracker.cs` |
| **Market context (strategy input)** | `TradingEngine.Domain/MarketData/MarketContext.cs`, `TradingEngine.Host/EngineWorker.cs` (builds it) |
| **Multi-symbol support** | `TradingEngine.Host/TradingLoop.cs`, `TradingEngine.Host/EngineWorker.cs`, `TradingEngine.Host/IndicatorSnapshotService.cs`, `TradingEngine.Host/StrategyBankService.cs`, `TradingEngine.Web/Pages/Backtests/Run.cshtml(.cs)` |
| **Multi-timeframe support** | Same as multi-symbol + `TradingEngine.Domain/Timeframe.cs` |
| **NetMQ (engine side)** | `TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs`, `.../NetMqMessageTransport.cs` |
| **News filter** | `TradingEngine.Risk/Filters/NewsFilter.cs` (stub — always false) |
| **Open issues (bug log)** | `docs/OPEN-ISSUES.md` |
| **Order dispatch** | `TradingEngine.Services/OrderDispatcher.cs` |
| **Order entry options** | `TradingEngine.Domain/OrderEntryOptions.cs`, `config/strategies/*.json` (`orderEntry` field) |

### P–S

| Feature | Key Files |
|---------|-----------|
| **Persistence (trades)** | `TradingEngine.Host/TradePersistenceHandler.cs`, `TradingEngine.Infrastructure/Persistence/Repositories/SqliteTradeRepository.cs` |
| **Pip calculation** | `TradingEngine.Services/PipCalculator.cs` (Distance, PipValuePerLot, GrossPnL, FloatingPnL, RMultiple) |
| **Position management (trailing etc.)** | `TradingEngine.Services/PositionTracker.cs`, `TradingEngine.Domain/PositionManagement/` |
| **Position lifecycle (kernel FSM)** | `TradingEngine.Domain/Engine/PositionLifecycle.cs` |
| **Position sizing** | `TradingEngine.Risk/PositionSizer.cs`, `TradingEngine.Risk/SizeModifierPipeline.cs` |
| **Position tracking** | `TradingEngine.Services/PositionTracker.cs` |
| **Prop firm rules** | `TradingEngine.Risk/PropFirmRuleValidator.cs`, `config/prop-firms/*.json` |
| **Protection mode** | `TradingEngine.Risk/RiskManager.cs` (`EnterProtectionMode`) |
| **Regime detection** | `TradingEngine.Risk/RegimeDetector.cs`, `config/regime.json` |
| **Repositories (SQLite)** | `TradingEngine.Infrastructure/Persistence/Repositories/` |
| **Risk gate (validation)** | `TradingEngine.Risk/RiskManager.cs` (`Validate`, `ValidateOrder`) |
| **Risk profiles** | `TradingEngine.Domain/RiskAndEquity/RiskProfile.cs`, `config/risk-profiles/*.json` |
| **Run plan (symbol/TF override)** | `TradingEngine.Domain/RunPlan.cs`, `TradingEngine.Host/EngineHostOptions.cs`, `TradingEngine.Host/StrategyBankService.cs` |
| **Schema (EF migrations)** | `TradingEngine.Infrastructure/Persistence/Migrations/` |
| **Session filter (weekend)** | `TradingEngine.Risk/Filters/SessionFilter.cs` |
| **Signal gate (cooldown)** | `TradingEngine.Services/SignalGateService.cs` |
| **SimulatedBrokerAdapter (venue)** | `TradingEngine.Infrastructure/Adapters/SimulatedBrokerAdapter.cs` |
| **Sizing policy** | `TradingEngine.Domain/RiskAndEquity/SizingPolicy.cs`, `config/sizing-policy.json` |
| **Skender indicators** | `TradingEngine.Infrastructure/Indicators/SkenderIndicatorService.cs`, `.../SkenderQuote.cs` |
| **SL/TP calculation** | `TradingEngine.Services/SlTpCalculator.cs`, `TradingEngine.Domain/PositionManagement/SlOptions.cs`, `.../TpOptions.cs` |
| **Slippage** | `TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs`, `.../SimulatedBrokerAdapter.cs` |
| **SSE streaming** | `TradingEngine.Web/Services/BacktestProgressStore.cs`, `TradingEngine.Web/Api/BacktestController.cs` |
| **Strategy — EMA Alignment** | `TradingEngine.Strategies/EmaAlignment/EmaAlignmentStrategy.cs` |
| **Strategy — MeanReversion** | `TradingEngine.Strategies/MeanReversion/MeanReversionStrategy.cs` |
| **Strategy — SessionBreakout** | `TradingEngine.Strategies/SessionBreakout/SessionBreakoutStrategy.cs` |
| **Strategy — TrendBreakout** | `TradingEngine.Strategies/TrendBreakout/TrendBreakoutStrategy.cs` |
| **Strategy bank (active selection)** | `TradingEngine.Host/StrategyBankService.cs`, `config/rotation.json` |
| **Strategy interface** | `TradingEngine.Domain/Interfaces/IStrategy.cs` |
| **Strategy registry (DI resolution)** | `TradingEngine.Host/StrategyRegistry.cs`, `TradingEngine.Domain/StrategyIdAttribute.cs` |
| **Swap (computation)** | `TradingEngine.Services/Helpers/TradeCostCalculator.cs` (`CountNightsHeld`) |
| **Symbol catalog** | `TradingEngine.Host/SymbolCatalog.cs`, `config/symbols.json` |
| **Symbol info (metadata)** | `TradingEngine.Domain/SymbolInfo/SymbolInfo.cs` |
| **Symbol registry** | `TradingEngine.Infrastructure/SymbolInfoRegistry.cs` |

### T–W

| Feature | Key Files |
|---------|-----------|
| **Tick synthesis (from bars)** | `TradingEngine.Infrastructure/HistoricalDataProvider.cs` (4 ticks at 0/25/50/75%) |
| **Trade cost calculation** | `TradingEngine.Services/Helpers/TradeCostCalculator.cs` |
| **Trade detail page** | `TradingEngine.Web/Pages/Trades/Detail.cshtml(.cs)` |
| **Trade persistence** | `TradingEngine.Host/TradePersistenceHandler.cs` |
| **Trade result** | `TradingEngine.Domain/Trading/TradeResult.cs` |
| **Trailing stop** | `TradingEngine.Services/TrailingStopService.cs`, `TradingEngine.Domain/PositionManagement/TrailingOptions.cs`, `TradingEngine.Domain/Engine/PositionLifecycle.cs` (kernel helpers) |
| **Web UI pages** | `TradingEngine.Web/Pages/` (Razor Pages: Dashboard, Trades, Performance, Backtests, Monitor, Index, Events, Strategies) |
| **Web UI JS (SignalR/SSE)** | `TradingEngine.Web/wwwroot/js/` |
| **Web DI / startup** | `TradingEngine.Web/Program.cs` |

---

## Part 2 — Process Walkthroughs

Key pipelines with files in the order they're called. Follow along to understand or debug.

### 2.1 Backtest end-to-end (UI click → trades in DB)

```
1. User clicks "Run Backtest"
   TradingEngine.Web/Pages/Backtests/Run.cshtml.cs    → form collects symbols, timeframes, dates

2. API receives request
   TradingEngine.Web/Api/BacktestController.cs        → Start() generates RunId, calls
   TradingEngine.Web/Services/BacktestOrchestrator.cs  → RunEngineReplayAsync()

3. Engine host created
   TradingEngine.Host/EngineHostFactory.cs             → builds IHost with DI
   TradingEngine.Host/EngineHostOptions.cs             → RunId, symbols, timeframes, config

4. Adapter loads bars
   TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs  → ConnectAsync reads from SQLite
   TradingEngine.Infrastructure/Persistence/Repositories/SqliteBarRepository.cs

5. Trading loop runs per bar
   TradingEngine.Host/TradingLoop.cs                   → ProcessBarAsync per bar

6. Indicators computed
   TradingEngine.Host/IndicatorSnapshotService.cs      → per-(symbol,tf,strategy) indicator values
   TradingEngine.Infrastructure/Indicators/SkenderIndicatorService.cs

7. Strategy evaluates
   TradingEngine.Strategies/*/*Strategy.cs             → Evaluate(MarketContext) → TradeIntent?

8. Entry planner
   TradingEngine.Services/Helpers/EntryPlanner.cs      → Plan(intent, OrderEntryOptions) → rewrites OrderType/LimitPrice

9. Order dispatch
   TradingEngine.Services/OrderDispatcher.cs           → DispatchAsync: validate, size, submit
   TradingEngine.Risk/RiskManager.cs                   → Validate, CalculateLotSize
   TradingEngine.Risk/PositionSizer.cs                 → lot size formula

10. Venue fills order
    TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs  → SubmitOrderAsync, fill at close

11. Cost computation
    TradingEngine.Services/Helpers/TradeCostCalculator.cs  → Compute(gross, commission, swap)

12. Position tracker
    TradingEngine.Services/PositionTracker.cs          → OnExecutionAsync → EngineReducer.Apply

13. Effect execution
    TradingEngine.Host/EffectExecutor.cs               → publishes TradeClosed, registers risk

14. Journal capture
    TradingEngine.Host/PipelineEventWriter.cs          → Record(DecisionRecord) → PipelineEvents table

15. Persistence (fire-and-forget)
    TradingEngine.Host/TradePersistenceHandler.cs      → Trades table
    TradingEngine.Host/BarEvaluationHandler.cs         → BarEvaluations table
    TradingEngine.Host/EquityPersistenceHandler.cs     → EquitySnapshots table

16. UI reads results
    TradingEngine.Web/Api/BacktestController.cs        → status, journal, stats endpoints
    TradingEngine.Web/Pages/Backtests/Report.cshtml.cs → renders journal, trades, equity chart
```

### 2.2 Strategy signal → fill

```
Strategy evaluates market data
  → TradingEngine.Strategies/*/*Strategy.cs            Evaluate(context)
    → reads context.Bars, .IndicatorValues
    → applies signal logic (breakout, RSI, EMA cross...)
    → returns new TradeIntent(symbol, dir, sl, tp, strategyId, reason, OrderType.Market, null)

EntryPlanner rewrites intent
  → TradingEngine.Services/Helpers/EntryPlanner.cs     Plan(intent, options)
    → reads strategy.Config.OrderEntry
    → Market → leaves intent as-is
    → LimitOffset → sets OrderType.Limit, LimitPrice = signal ± offset
    → re-derives SL/TP off planned entry

SignalGate checks cooldown
  → TradingEngine.Services/SignalGateService.cs        Check(strategyId, direction)
    → blocks if same direction too recently for this strategy

RiskManager validates
  → TradingEngine.Risk/RiskManager.cs                  Validate(intent, equity, profile)
    → 8 checks: protection, daily DD, max DD, positions, exposure, news, weekend
    → ValidateOrder: worst-case projection, budget downsizing

PositionSizer calculates lots
  → TradingEngine.Risk/PositionSizer.cs                Calculate(equity, risk%, slPips, pipValue, scale, limits)
    → rawLots = riskAmount / (slPips × pipValue)
    → scaled = rawLots × drawdownScaleFactor
    → stepped = floor(stepped / lotStep) × lotStep

OrderDispatcher submits
  → TradingEngine.Services/OrderDispatcher.cs          DispatchAsync
    → builds OrderRequest(OrderId, Symbol, Direction, Lots, OrderType, LimitPrice, SL, TP)
    → broker.SubmitOrderAsync(orderRequest)

Venue fills
  → TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs  SubmitOrderAsync
    → market: immediate fill at bar close
    → limit: rests until price reached or expiry
```

### 2.3 Position close → cost → journal

```
Position closes (SL hit, TP hit, force close, or limit expiry)
  ┌─ Market exit:
  │   BacktestReplayAdapter detects SL/TP hit on bar low/high
  │   SimulatedBrokerAdapter checks per tick
  └─ Force exit:
      PositionTracker.RequestForceCloseAllAsync() on breach

ExecutionEvent emitted
  → adapter.ClosePositionAsync(...)
    calls TradeCostCalculator.Compute(...)
      → TradingEngine.Services/Helpers/TradeCostCalculator.cs
        gross  = PipCalculator.GrossPnL(entry, exit, dir, lots, symbolInfo, crossRate)
        comm   = lots × commissionPerLotPerSide × 2
        swap   = nightsHeld × swapRate(dir) × lots (×3 on triple day)
        net    = gross − comm − swap
    stamps ExecutionEvent(GrossProfit, Commission, Swap, NetProfit)

PositionTracker processes
  → TradingEngine.Services/PositionTracker.cs          OnExecutionAsync(execEvent)
    → is this a close? (OrderId not in _pendingOrders)
    → DetermineExitReason: SL / TP / FORCE / DailyDD / MaxDD
    → EngineReducer.Apply(state, event) → effects
    → TradingEngine.Domain/Engine/PositionLifecycle.cs Apply
    → EffectExecutor.HandlePublishTradeClosed
      → pubs TradeClosed(tradeResult, runId)
      → TradingEngine.Host/EffectExecutor.cs

Journal records
  → TradingEngine.Host/PipelineEventWriter.cs          Record
    → NormalizedKind via JournalNormalizer
    → TradingEngine.Services/Helpers/JournalNormalizer.cs
      OrderFilled with close reason → CLOSE
      OrderCancelled → ENTRY_EXPIRED
    → PipelineEvents table

Cost detail on close journal:
  record.Detail = { exit, gross, commission, swap, net }
```

### 2.4 Config: JSON → seed → DB → effective → run

```
JSON source files
  → config/strategies/*.json                           e.g. trend-breakout.json
  → TradingEngine.Domain/OrderEntryOptions.cs          shape (parsed from orderEntry)

Seeded to DB on first run
  → TradingEngine.Infrastructure/Persistence/StrategyConfigSeeder.cs  SeedAsync
    reads config/strategies/*.json
    upserts to StrategyConfigs table (idempotent)
  → TradingEngine.Infrastructure/Persistence/SqliteStrategyConfigStore.cs  UpsertAsync

Host loads from DB
  → TradingEngine.Host/ConfigLoader.cs                Load()
    reads risk profiles, prop-firm rules from JSON
    reads strategy configs from IStrategyConfigStore (DB)
    returns LoadedConfig

Per-run overrides (optional)
  → TradingEngine.Host/EngineHostOptions.cs            StrategyOverrides, RunPlan
  → TradingEngine.Services/Helpers/EffectiveConfigResolver.cs  Resolve
    deep-merges: stored default ← per-run override ← run plan
    returns EffectiveConfigEntry

Strategies activated
  → TradingEngine.Host/StrategyRegistry.cs             Resolves [StrategyId] → Type
  → TradingEngine.Host/ConfigLoader.cs                Filters by ActiveStrategyIds
  → TradingEngine.Host/StrategyBankService.cs          GetActive(symbol, tf, regime, runPlan)
    filters by regime, symbol, timeframe per RunPlan
```

### 2.5 Venue routing

```
BacktestOrchestrator.Start()
  reads CTrader:UseForBacktest from config

TRUE → cTrader path (needs credentials)
  → BacktestOrchestrator.RunAsync()
    → TradingEngine.CTraderRunner/BacktestRunner.cs   launch ctrader-cli subprocess
    → TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs  engine side transport
    → TradingEngine.Adapters.CTrader/TradingEngineCBot.cs   cBot inside cTrader
    → Lock-step protocol (DEALER/ROUTER via NetMQ)
    → Ports 15555/15556

FALSE → Replay path (default, credential-free)
  → BacktestOrchestrator.RunEngineReplayAsync()
    → TradingEngine.Host/EngineHostFactory.cs         inner IHost
    → TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs
    → Bars from SQLite Bars table (must be pre-seeded)
    → Cost-aware fills, limit order support

Test harness path (not UI)
  → tests/TradingEngine.Tests.Simulation/
  → EngineHarnessBuilder → FakeVenue (in-memory, no DB, no IHost)
  → ReplayTestHarness → BacktestReplayAdapter (real IHost + SQLite)
  → CtraderTestHarness → CTraderBrokerAdapter (real ctrader CLI + NetMQ)
```

### 2.6 Risk validation pipeline

```
TradeIntent arrives
  → TradingEngine.Services/OrderDispatcher.cs          DispatchAsync

Pre-trade gate (hard reject)
  → TradingEngine.Risk/RiskManager.cs                  Validate(intent, equity, profile)
    checks in order:
    1. PROTECTION_MODE_ACTIVE   → TradingAllowed == false
    2. DAILY_DD_LIMIT           → TradingEngine.Risk/DrawdownTracker.cs.GetDailyDrawdown()
    3. MAX_DD_LIMIT             → DrawdownTracker.GetMaxDrawdown()
    4. MAX_POSITIONS            → _openPositions.Count >= MaxConcurrentPositions
    5. STRATEGY_MAX_POSITIONS   → per-strategy count
    6. MAX_EXPOSURE             → TradingEngine.Risk/CurrencyExposureTracker.cs
    7. NEWS_WINDOW              → TradingEngine.Risk/Filters/NewsFilter.cs (stub)
    8. WEEKEND_RESTRICTION      → TradingEngine.Risk/Filters/SessionFilter.cs

Smart avoidance (ValidateOrder)
  → TradingEngine.Risk/RiskManager.cs                  ValidateOrder
    worst-case projection: candidateLoss + openLosses against daily/max floor
    budget downsizing: halves lots until within daily budget
    drawdown scaling: TradingEngine.Risk/DrawdownScaler.cs

Lot sizing
  → TradingEngine.Risk/PositionSizer.cs                Calculate(...)
    → sees RiskProfile.LotSizingMethod → dispatch to one of 5 methods
    → TradingEngine.Risk/SizeModifierPipeline.cs       optional chain of modifiers

Breach watchdog (async, on equity update)
  → TradingEngine.Host/AccountProcessor.cs             HandleAsync(accountUpdate)
    if DailyDrawdownUsed ≥ MaxDailyLossPercent × FlattenAtFraction
    → RiskManager.EnterProtectionMode()
    → PositionTracker.RequestForceCloseAllAsync()
```

### 2.7 Multi-symbol bar processing

```
Bars arrive per (Symbol, Timeframe)
  → venue writes to BarStream channel
  → EngineWorker.ProcessBarsAsync drains bar channel
  → _bars dictionary accumulates per (symbol,tf): ConcurrentDictionary + lock, capped at 500

Per bar closure:
  TradingEngine.Host/TradingLoop.cs                    ProcessBarAsync(bar)

Indicator snapshot
  → TradingEngine.Host/IndicatorSnapshotService.cs     BuildStrategyIndicatorValues(symbol, strategy)
    key: (symbol, timeframe, type, period, param)
    returned per strategy → no cross-strategy bleed

Regime detection
  → TradingEngine.Risk/RegimeDetector.cs               Detect(symbol, tf, indicators)
  → TradingEngine.Host/StrategyBankService.cs          GetActive(symbol, tf, regime, runPlan)
    filters by regime and RunPlan

Strategy evaluation per active strategy
  → MarketContext(symbol, closeTick, barSnapshot, strategyIndicators, now)
  → strategy.Evaluate(context) → TradeIntent?
  → EntryPlanner.Plan(intent) → rewrites entry
  → OrderDispatcher.DispatchAsync
```

---

## Part 3 — Test File to Code Mapping

Mapping common test files to the production code they validate:

| Test | Validates |
|------|-----------|
| `tests/.../Unit/RiskManagerTests.cs` | `src/TradingEngine.Risk/RiskManager.cs` |
| `tests/.../Unit/PositionSizerTests.cs` | `src/TradingEngine.Risk/PositionSizer.cs` |
| `tests/.../Unit/PipCalculatorTests.cs` | `src/TradingEngine.Services/PipCalculator.cs` |
| `tests/.../Unit/SlTpCalculatorTests.cs` | `src/TradingEngine.Services/SlTpCalculator.cs` |
| `tests/.../Unit/OrderDispatcherTests.cs` | `src/TradingEngine.Services/OrderDispatcher.cs` |
| `tests/.../Unit/TradeCostCalculatorTests.cs` | `src/TradingEngine.Services/Helpers/TradeCostCalculator.cs` |
| `tests/.../Unit/JournalNormalizerTests.cs` | `src/TradingEngine.Services/Helpers/JournalNormalizer.cs` |
| `tests/.../Unit/EffectiveConfigResolverTests.cs` | `src/TradingEngine.Services/Helpers/EffectiveConfigResolver.cs` |
| `tests/.../Unit/PositionLifecycleTests.cs` | `src/TradingEngine.Domain/Engine/PositionLifecycle.cs` |
| `tests/.../Unit/EngineReducerTests.cs` | `src/TradingEngine.Domain/Engine/EngineReducer.cs` |
| `tests/.../Unit/DrawdownReducerTests.cs` | `src/TradingEngine.Domain/Engine/DrawdownReducer.cs` |
| `tests/.../Unit/TradingGovernorServiceTests.cs` | `src/TradingEngine.Risk/TradingGovernorService.cs` |
| `tests/.../Unit/ExitReasonTests.cs` | `src/TradingEngine.Domain/Trading/ExitReason.cs` |
| `tests/.../Simulation/FtmoGoldenJourneyTests.cs` | Full pipeline: `RiskManager` + `DrawdownTracker` + `EngineReducer` |
| `tests/.../Simulation/BacktestReplayTests.cs` | `BacktestReplayAdapter` + `ReplayTestHarness` |
| `tests/.../Integration/DIValidationTests.cs` | `EngineHostFactory` DI wiring |
| `tests/.../Integration/WebSmokeTests.cs` | All Razor Pages + API endpoints |
| `tests/.../Architecture/EnginePurityTests.cs` | Layer boundary enforcement |
