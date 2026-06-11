# Shamshir Trading Engine — System Reference

**Written**: 2026-06-11 (post-Iteration 17)
**Branch**: `iter/17-deterministic-pipeline`
**For**: Any implementing agent needing to understand the full system

---

## 1. What Is This System?

Shamshir is a **prop-firm algorithmic trading engine** targeting .NET 10. It runs automated trading strategies with FTMO-style risk rules, position sizing, and drawdown tracking. Backtests execute via cTrader's CLI using a custom cBot (`TradingEngineCBot.cs`), communicating over NetMQ (ZeroMQ/.NET).

**Two backtest paths:**

| Path | How | Used for |
|------|-----|----------|
| **cTrader** (production) | `ctrader-cli.exe` runs cBot in-process. Engine communicates via NetMQ DEALER↔ROUTER (lock-step protocol). Needs credentials. | Final verification, real-world equivalent |
| **DB Replay** (development) | `BacktestReplayAdapter` reads bars from SQLite. Instant-fills at close price. No cTrader, no credentials. | Fast iteration, CI, unit tests |

**Architecture:**
```
Web UI (Razor Pages) → BacktestOrchestrator → EngineHostFactory → inner IHost
    → EngineWorker (EngineMode.Backtest) → strategies → OrderDispatcher
    → NetMQBrokerAdapter (DEALER↔ROUTER lock-step) → cBot in cTrader
```

---

## 2. Position Sizing — End-to-End

### Flow

```
Strategy.Evaluate() → TradeIntent
  → OrderDispatcher.DispatchAsync(intent, equity, currentMid, broker, ct)
    ├─ riskManager.Validate(intent, equity, profile)  → violations? → BLOCK
    ├─ PipCalculator.Distance(entry, sl, symbolInfo)    → slDistance (Pips)
    ├─ PipCalculator.PipValuePerLot(symbolInfo, price, crossRate)
    ├─ DrawdownScaler.ComputeScaleFactor(currentDD%, maxLimit, threshold, floor)
    └─ PositionSizer.Calculate(equity, risk%, slDistance, pipValue, scale, maxLots, minLots, step)
        → lots (decimal, floored to lot step)
  → broker.SubmitOrderAsync(orderRequest, ct)          → orderId (Guid)
  → PositionTracker.TrackOrder(orderId, request, riskAmount)
  → returns OrderContext(orderId, lots, riskAmount, profile)
```

### Lot Size Formula

```
riskAmount = equity × riskPerTradePercent
pipValue   = PipCalculator.PipValuePerLot(symbol, price, crossRate)
rawLots    = riskAmount / (slPips × pipValue)
scaledLots = rawLots × drawdownScaleFactor
clamped    = min(scaledLots, maxLots)
stepped    = floor(clamped / lotStep) × lotStep
finalLots  = max(stepped, minLots)
```

### Pip Value Calculation (three-branch)

```
If quoteCurrency == accountCurrency (e.g. EUR/USD, account = USD):
    pipValue = pipSize × contractSize  (e.g. 0.0001 × 100000 = $10)

If baseCurrency == accountCurrency (e.g. USD/JPY, account = USD):
    pipValue = (pipSize × contractSize) / currentPrice

Otherwise (cross pair, e.g. EUR/GBP, account = USD):
    pipValue = (pipSize × contractSize) × crossRate(quote → account)
```

### Drawdown Scaler

Linear interpolation between 1.0 (below threshold) and scaleFloor (at limit):
```
ddRatio = currentDrawdown / maxDrawdownLimit
if ddRatio <= threshold: return 1.0
if ddRatio >= 1.0:       return scaleFloor
return 1.0 - ((ddRatio - threshold) / (1.0 - threshold)) × (1.0 - scaleFloor)
```

Standard profile: threshold=0.5, floor=0.5. At 50% DD usage, scale stays 1.0. At 100% usage, scale = 0.5.

### Lot Sizing Methods

| Method | Description | Config Property |
|--------|-------------|-----------------|
| `PercentRisk` (default) | Risk a % of equity per trade | `riskPerTradePercent` |
| `FixedLots` | Always trade a fixed lot size | `fixedLots` |
| `FixedDollarRisk` | Risk a fixed dollar amount per trade | `fixedDollarRisk` |
| `KellyFraction` | Kelly criterion: % risk × kelly fraction | `kellyFraction` |
| `AntiMartingale` | Increase size after wins | `antiMartingaleMultiplier` |

---

## 3. Risk Management — End-to-End

### Flow

```
TradeIntent → RiskManager.Validate(intent, equity, profile) → List<RiskViolation>

Checks (in order):
1. PROTECTION_MODE_ACTIVE    — Trading suspended after breach
2. DAILY_DD_LIMIT            — Daily drawdown ≥ maxDailyLossPercent (FTMO)
3. MAX_DD_LIMIT              — Max drawdown ≥ maxTotalLossPercent (FTMO)
4. MAX_POSITIONS             — Global concurrent position limit from RiskProfile
5. STRATEGY_MAX_POSITIONS    — Per-strategy position limit
6. MAX_EXPOSURE              — (currentRisk + newRisk) / equity > maxExposurePercent
7. NEWS_WINDOW               — High-impact news window active (if FTMO rule set)
8. WEEKEND_RESTRICTION       — Weekend close approaching (if FTMO rule set)

If any violation: OrderDispatcher returns null → signal blocked → no order sent.
```

### Position Tracking

```
PositionTracker.OnExecutionAsync(execEvent)
  → execEvent.OrderId NOT in _pendingOrders
    → ClosePositionAsync()  → calculates PnL, determines exit reason (SL/TP/FORCE)
      → publishes TradeClosed event → TradePersistenceHandler → DB
  → execEvent.OrderId IN _pendingOrders
    → accumulate partial fills → when full:
      → Create Position → Register with RiskManager + PositionManager
      → Apply trailing stop/breakeven via PositionManager.Evaluate()
```

### Position Lifecycle States

```
Active → BreakevenSet → Trailing → Closed
```

### Trailing Stop Methods

| Method | Description |
|--------|-------------|
| `StepPips` | Advance SL by fixed pip step when price moves favorably |
| `AtrMultiple` | Advance SL = (high/low water mark) - (ATR × multiplier) |
| `BreakevenThenTrail` | Move to breakeven first, then trail after |

---

## 4. FTMO Prop Firm Rules

### Config: `config/prop-firms/ftmo-standard.json`

| Rule | Standard | Aggressive |
|------|----------|------------|
| Max Daily Loss | **5%** | **8%** |
| Max Total Loss | **10%** | **15%** |
| Profit Target | **10%** | **20%** |
| Min Trading Days | 4 | 4 |
| Drawdown Type | Fixed (from initial) | Fixed (from initial) |
| Daily DD Base | InitialBalance | InitialBalance |
| News Block | 30min before, 15min after (High impact) | Same |
| Weekend Holding | No | No |
| Weekend No-Open | After 20:00 UTC Friday | Same |
| ForceCloseOnBreach | No | No |
| Daily Reset | 22:00 UTC (Europe/Prague) | Same |
| Protection Reset | Next Trading Day | Same |

**Daily drawdown** is calculated as: `(InitialBalance - currentEquity) / InitialBalance` (when DailyDdBase = InitialBalance).

**Max drawdown** is calculated as: `(equityBase - currentEquity) / equityBase`, where equityBase = InitialBalance (Fixed mode) or PeakEquity (Trailing mode).

When max total drawdown is breached → `EnterProtectionMode()` → `TradingAllowed = false` → all future signals blocked.

### Risk Profiles (config/risk-profiles/)

| Profile | Risk/Trade | Max Daily DD | Max Total DD | Max SL | Max Exposure | DD Scale | Max Positions |
|---------|------------|--------------|--------------|--------|--------------|----------|---------------|
| Conservative | 0.5% | 3% | 6% | 50 pips | 3% | starts 50%, min 50% | 2 |
| Standard | 1.0% | 4% | 8% | 100 pips | 5% | starts 50%, min 50% | 3 |
| Aggressive | 2.0% | 5% | 10% | 150 pips | 10% | starts 75%, min 25% | 5 |

All profiles reference `propFirmRuleSetId: "ftmo-standard"`. The FTMO rules are the hard guardrails; the risk profile is the strategy-level sizing.

---

## 5. Multi-Symbol Support

### How it works

The UI allows selecting up to 12 symbols (checkboxes) and 6 timeframes. The first checked symbol is the **primary** (passed to cTrader's `--symbol` flag, which determines which symbol's ticks drive the simulation loop). All checked symbols are passed via `--SymbolString=EURUSD,GBPUSD,...` to the cBot.

The cBot calls `MarketData.GetBars(tf, sym)` for each symbol×period pair and attaches `OnBarClosed` handlers. cTrader's backtest provides data for all symbols. Bars from different symbols arrive sequentially (lock-step ensures one-at-a-time processing).

The engine's `EngineHostFactory` registers ALL requested symbols via the `SymbolCatalog` (resolved from `config/symbols.json`). The built-in catalog has 16 symbols with correct pip sizes, contract sizes, base/quote currencies, and spreads.

### Verified

Multi-symbol test (EURUSD + GBPUSD, H1, 3-day):
- EURUSD: **19 trades**
- GBPUSD: **19 trades**
- Unified risk management active — `MAX_POSITIONS` violations block signals across both symbols

### cTrader Limitation

The `--symbol` CLI flag takes ONE symbol (the primary simulation driver). Secondary symbols get bars via `MarketData.GetBars()` but their ticks don't fire `OnTick()`. For lock-step, this is fine — commands execute in the bar barrier, not via tick drain.

---

## 6. Strategy Implementations

### Common Interface
```csharp
public interface IStrategy {
    string Id { get; }
    TradeIntent? Evaluate(MarketContext context);
    void OnTradeResult(TradeResult result);
    IReadOnlyList<IndicatorRequest> RequiredIndicators { get; }
    int RequiredBarCount { get; }
}
```

Every strategy receives a `MarketContext` containing the symbol, latest tick, OHLC bars keyed by timeframe, pre-computed indicator values, and engine time.

### TrendBreakout (`config/strategies/trend-breakout.json`)

- **Signal**: Break of 20-bar high (Long) or low (Short), confirmed above/below EMA50
- **SL/TP**: ATR-based (1.5× ATR offset), TP = 2.0× SL (RR multiple)
- **Indicators**: EMA(50), ATR(14)
- **Symbols**: EURUSD, GBPUSD (H1)
- **Risk**: standard profile

### MeanReversion (`config/strategies/mean-reversion.json`)

- **Signal**: RSI < 35 (oversold Long) or RSI > 65 (overbought Short), price within 0.2% of bar extremum
- **SL/TP**: ATR-based (1.5× ATR offset), TP = 1.0× SL
- **Indicators**: RSI(14), Bollinger Bands(20, 2.0σ), ATR(14)
- **Symbols**: EURUSD, GBPUSD (H1)
- **Risk**: standard profile

### SessionBreakout (`config/strategies/session-breakout.json`)

- **Signal**: Records 05:00-07:00 UTC session range, trades breakouts until 09:00 UTC
- **SL/TP**: ATR-based (1.5× ATR offset), TP = 2.0× SL
- **Indicators**: ATR(14)
- **Symbols**: EURUSD, GBPUSD (H1)
- **Risk**: standard profile
- **Flatten**: All positions closed at 12:00 UTC

### EMA Alignment (`config/strategies/ema-alignment.json`)

- **Signal**: Fast EMA(20) crosses above Slow EMA(50) (Long) or below (Short), price same side as crossover
- **SL/TP**: ATR-based (1.5× ATR offset), TP = 2.0× SL
- **Indicators**: EMA(20), EMA(50), ATR(14)
- **Symbols**: EURUSD, GBPUSD (H1)
- **Risk**: standard profile

---

## 7. UI Features

### Pages

| Route | Description |
|-------|-------------|
| `/` | Dashboard — trading status, daily/max DD %, equity curve chart |
| `/trades` | Paginated trades list (50/page), clickable rows |
| `/trades/{id}` | Trade detail — all fields, PnL gradient chart |
| `/performance` | Total trades, win rate, net PnL, avg hold, equity chart |
| `/backtests` | Merged list of active + completed backtests |
| `/backtests/run` | New backtest form — checkbox multi-symbol (12 symbols, 6 timeframes), date picker, balance |
| `/backtest/{runId}` | Live progress via SSE — color-coded log, counters, result table on completion |
| `/backtests/detail?runId=` | Completed backtest detail + per-strategy breakdown |
| `/events` | Last 100 engine events |

### API Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/backtest/start` | Start backtest → returns `{runId, status}` |
| `GET` | `/api/backtest/{runId}/status` | Current status + result |
| `GET` | `/api/backtest/{runId}/logs` | Raw log lines |
| `GET` | `/api/backtest/{runId}/journal` | Pipeline stage funnel + event list |
| `GET` | `/api/backtest/{runId}/stream` | Server-Sent Events for live progress |

### Design

Dark theme (GitHub-style: `#0d1117`), responsive grid, Chart.js for equity curves, SSE for real-time streaming, color-coded statuses.

---

## 8. Test Infrastructure

### Unit Tests: 87 tests (credential-free)

**Categories:** RiskManager (12), DrawdownTracker (14), PositionSizer (5), PipCalculator (6), SlTpCalculator (8), TrailingStop (2), ExcursionTracker (2), ExitReason (4), DrawdownScaler (4), OrderDispatcher, PositionManager, TypedEventBus, SimulatedBroker, SessionFilter, PersistenceService, TrendBreakoutStrategy (3), RiskProfileResolver, RiskManagerPhase3A, PipCalculatorPhase3A

### Simulation Tests (some credential-based)

| Test | Credentials | Verifies |
|------|-------------|----------|
| `ReplayBacktest_FullPipeline` | No | 50-bar DB replay in-process |
| `InProcessEngineSmokeTests` | No | Inner host DI starts/stops cleanly |
| `TrendBreakoutScenarios` | No | CSV data → strategy signals |
| `MultiStrategyScenarios` | No | Two strategies, same symbol |
| `DrawdownScenarios` | No | Daily/max DD halts, recovery |
| `NetMQBridgeTest` | No | Engine subprocess NetMQ |
| `EurUsd_H1_3Days` | **Yes** | 3-day EURUSD end-to-end |
| `GbpUsd_H1_30Days` | **Yes** | 30-day GBPUSD end-to-end |
| `EurUsd_GbpUsd_H1_3Days_MultiSymbol` | **Yes** | Multi-symbol EURUSD+GBPUSD |
| `EurUsd_H1_30Days` | **Yes** | 30-day EURUSD |

### Harness Files

- `FakeCBot.cs` — Credential-free cBot simulator for lock-step testing
- `ReplayTestHarness.cs` — DB replay test builder
- `EngineTestHarness.cs` — In-process engine test builder
- `DrawdownTestHarness.cs` — Drawdown isolation test harness
- `AlwaysSignalStrategy.cs` — Test strategy that always fires signals

---

## 9. Current UI Features

### Multi-Symbol Selection
- 12 symbol checkboxes (EURUSD through XAGUSD)
- 6 timeframe checkboxes (H1, M15, M5, M1, D1, H4)
- First checked symbol = primary (cTrader's `--symbol`)
- Date range picker, initial balance input

### Live Progress
- SSE streaming with color-coded logs
- Real-time counters (Bars, Signals, Trades)
- Result table on completion (Net Profit, Max DD%, Total/Winning Trades, Win Rate)
- "View Trades" link on completion

### Backtest Detail
- Summary (symbol, period, date range, balance, algo hash)
- Strategy breakdown per strategy (bars, signals, trades, wins, losses, win rate, top rejection reasons)

---

## 10. Known Issues

### Fixed in Iteration 17
- NetMQ thread-affinity bug (orders silently lost) → `NetMQQueue<T>` on poller thread
- Sleep-based sync (600ms + 10×500ms heartbeats) → `hello`/`hello_ack` handshake
- DI block copied in 3 places → `EngineHostFactory` single composition root
- EngineMode type-sniffing → explicit `EngineMode` parameter
- Hardcoded EURUSD SymbolInfo → `config/symbols.json` + `SymbolCatalog`
- cBot close position Guid/long mismatch → `_positionMap` reverse-lookup
- Symbol defaulting to EURUSD for all backtests → `RunModel.OnPost` + `StartRequest` fix
- EF Core SQL log flood → `.AddFilter("Microsoft.EntityFrameworkCore", Warning)`
- PUB/SUB framing desync → resilient `OnSubReceive` with `TryReceiveFrameString`
- Dual execution consumer → drain both channels

### Remaining
- **PipelineEvents table**: No EF migration generated yet — entity, mapping, writer, repository all ready, but `OnModelCreating` config needs a proper migration run (`dotnet ef migrations add`). Currently created by raw SQL `EnsureCreated` in Program.cs.
- **BacktestReplayAdapter regression**: Not tested after Phase C lock-step changes. `ReplayBacktest_FullPipeline` should pass (default no-op for `CompleteBarAsync`).
- **Live mode**: Not re-tested. EngineWorker changes focused on Backtest mode.
- **`TradeResult` zeroed fields**: Commissions, swap, MAE/MFE, R-multiple are zeroed in `PositionTracker.ClosePositionAsync`. cBot now sends `GrossProfit`/`NetProfit` in exec payloads (C1 fix), but engine doesn't use them yet.
- **`_processedExecutionIds` unbounded**: HashSet never pruned. C3's explicit `kind` field enables cleanup but not yet implemented.
- **Raw `ALTER TABLE` in `Program.cs`**: Schema changes via raw SQL instead of EF migrations (STD-07).
- **`await Task.Delay(5_000)` in replay path**: Should be replaced with completion signal from the unified inbound stream.
- **`ClosePositionAsync` `Price(1m)` fallback**: Synthetic fill at price 1.0 still exists as fallback in the adapter. Should be removed (with barrier, disconnected cBot is a hard error).
- **SSE cancellation propagates to look like a crash**: When browser tab closes, SSE reader cancels → `OperationCanceledException` caught in controller, but the backtest continues. Visual Studio may break on the exception.
- **cBot `Print()` output not visible in test stdout**: cTrader routes `Print()` to its own log system, not stdout. CBOT lines: 0 in test output is expected.

---

## 11. Key Files Reference

### Domain (`src/TradingEngine.Domain/`)
| File | Purpose |
|------|---------|
| `Interfaces/IBrokerAdapter.cs` | Broker adapter contract (SubmitOrder, ClosePosition, CompleteBarAsync, streams) |
| `Interfaces/IStrategy.cs` | Strategy contract (Evaluate, OnTradeResult, required indicators) |
| `Interfaces/IRiskManager.cs` | Risk manager contract (Validate, CalculateLotSize, UpdateEquityLevels) |
| `Interfaces/IPipelineJournal.cs` | Journal contract for pipeline events |
| `Interfaces/IPipelineEventRepository.cs` | Pipeline event persistence |
| `Interfaces/ISymbolInfoRegistry.cs` | Symbol metadata lookup |
| `Trading/TradeIntent.cs` | Signal-to-order bridge (Symbol, Direction, SL, TP, StrategyId, Reason) |
| `Trading/TradeResult.cs` | Completed trade (PnL, R-multiple, MAE/MFE, exit reason) |
| `Trading/Position.cs` | Open position state |
| `RiskAndEquity/RiskProfile.cs` | Per-strategy risk configuration |
| `RiskAndEquity/PropFirmRuleSet.cs` | FTMO rule set (daily/max loss %, profit target, news/weekend rules) |
| `RiskAndEquity/EquitySnapshot.cs` | Per-update equity state |
| `RiskAndEquity/RiskState.cs` | Current risk status (trading allowed, DD levels, protection) |
| `MarketData/MarketContext.cs` | What strategies see (bars, indicators, tick, time) |
| `SymbolInfo/SymbolInfo.cs` | Symbol metadata (pip size, contract size, currencies, spread) |

### Host (`src/TradingEngine.Host/`)
| File | Purpose |
|------|---------|
| `EngineWorker.cs` | Main engine loop (Backtest: single-threaded lock-step; Live: 4 concurrent loops) |
| `EngineHostFactory.cs` | Single composition root — all DI, `WireEventHandlers()`, `WireRiskRules()` |
| `EngineHostOptions.cs` | Run configuration (RunId, Mode, AdapterFactory, DbPath, Symbols, LogLevel) |
| `SymbolCatalog.cs` | Loads `config/symbols.json`, resolves symbol metadata |
| `PipelineEventWriter.cs` | Channel-backed background flusher for pipeline journal |
| `StrategyRegistry.cs` | Scans assembly for strategies, instantiates via config |
| `BarEvaluationHandler.cs` | Background flusher for bar evaluation persistence |
| `TradePersistenceHandler.cs` | Background flusher for trade persistence |
| `EquityPersistenceHandler.cs` | Background flusher for equity snapshot persistence |
| `CrossRateStore.cs` | Mutable cross-rate store (GBP→USD, JPY→USD) |
| `BacktestProgressEvent.cs` | Progress event DTO for SSE streaming |
| `EngineRunContext.cs` | Run correlation DTO |
| `ConfigLoader.cs` | Loads all `config/*.json` files |

### Risk (`src/TradingEngine.Risk/`)
| File | Purpose |
|------|---------|
| `RiskManager.cs` | Central orchestrator — validates signals, calculates lot sizes, tracks positions |
| `DrawdownTracker.cs` | Tracks peak equity, daily/max drawdown fractions |
| `PositionSizer.cs` | Lot size calculation (5 methods) |
| `DrawdownScaler.cs` | Linear interpolation between 1.0 and scaleFloor |
| `PropFirmRuleValidator.cs` | Validates FTMO daily/max loss limits |
| `Filters/NewsFilter.cs` | Stub — always returns false (no news blocking) |
| `Filters/SessionFilter.cs` | Weekend detection |

### Infrastructure (`src/TradingEngine.Infrastructure/`)
| File | Purpose |
|------|---------|
| `Adapters/NetMQBrokerAdapter.cs` | NetMQ transport — SUB (telemetry), ROUTER (lock-step bar/command flow) |
| `Adapters/BacktestReplayAdapter.cs` | DB bar replay — instant-fills at close price |
| `Persistence/TradingDbContext.cs` | EF Core context — 9 DbSets (Trades, Orders, Positions, Events, EquitySnapshots, Bars, BarEvaluations, BacktestRuns, PipelineEvents) |
| `Persistence/Repositories/` | SQLite-backed repositories for all entities |

### Web (`src/TradingEngine.Web/`)
| File | Purpose |
|------|---------|
| `Services/BacktestOrchestrator.cs` | Backtest lifecycle — start, run, cancel, status, trade stats |
| `Services/BacktestJournal.cs` | Unified SSE + log queue writer |
| `Services/BacktestProgressStore.cs` | Per-runId SSE channel store |
| `Services/BacktestQueryService.cs` | Query backtest runs, strategy breakdown, equity curve |
| `Api/BacktestController.cs` | REST endpoints (start, status, logs, journal, SSE stream) |
| `Pages/Backtests/Run.cshtml` | Multi-symbol backtest form (12 checkboxes, 6 timeframes) |
| `Pages/Backtests/Progress.cshtml` | Live SSE progress with counters and result table |
| `Pages/Backtests/Detail.cshtml` | Completed backtest detail with per-strategy breakdown |

### cBot (`src/TradingEngine.Adapters.CTrader/`)
| File | Purpose |
|------|---------|
| `TradingEngineCBot.cs` | cBot running inside cTrader — lock-step protocol, bar handlers, order execution |

### Configuration (`config/`)
| File | Purpose |
|------|---------|
| `symbols.json` | 16 symbol definitions (pip size, contract size, currencies, spread) |
| `prop-firms/ftmo-standard.json` | FTMO rules (5% daily, 10% max, 10% profit target) |
| `prop-firms/ftmo-aggressive.json` | FTMO aggressive (8% daily, 15% max, 20% target) |
| `risk-profiles/standard.json` | 1% risk/trade, 4% daily DD, 3 max positions |
| `strategies/trend-breakout.json` | TrendBreakout parameters |
| `strategies/mean-reversion.json` | MeanReversion parameters |
| `strategies/session-breakout.json` | SessionBreakout parameters |
| `strategies/ema-alignment.json` | EMA Alignment parameters |

---

## 12. Lock-Step Protocol (Iteration 17)

The cBot and engine communicate via a deterministic lock-step protocol over NetMQ DEALER↔ROUTER:

```
cBot → engine   {type:"hello", v:1, symbols:[..], periods:[..], barsLoaded:N}
engine → cBot   {type:"hello_ack", v:1}

cBot → engine   {type:"bar", seq:N, symbol, period, openTime, o,h,l,c, volume, simTime, account}
cBot BLOCKS until:
engine → cBot   {type:"bar_done", seq:N, commands:[{submit_order,...},{close_position,...}]}
cBot executes commands synchronously at bar N's simulated time:
cBot → engine   {type:"bar_result", seq:N, execs:[{clientOrderId,kind,state,fillPrice,...}], account}

cBot → engine   {type:"exec", ...}     ← async SL/TP hits between bars

cBot → engine   {type:"stats", barsSent:N, cmdsReceived:N, ordersExecuted:N, execsSent:N}
engine → cBot   {type:"shutdown"}
```

**Threading**: All ROUTER sends go through `NetMQQueue<T>` on the poller thread. cBot blocks in `OnBarClosed` → cTrader pauses simulation → deterministic execution.

**Reconciliation**: On `stats` message, engine compares its counters (bars received, commands sent, execs received) against cBot counters. Mismatches logged as `RECONCILE` errors.

Full protocol spec: `docs/iterations/iter-17/PROTOCOL.md`

---

## 13. Build and Test Commands

```powershell
# Full build
dotnet build --no-incremental

# Unit tests (87 tests, no credentials)
dotnet test tests/TradingEngine.Tests.Unit --no-build

# Single simulation test (needs credentials)
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "EurUsd_H1_3Days"

# Multi-symbol test (needs credentials)
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "MultiSymbol"

# Run web UI
dotnet run --project src/TradingEngine.Web
# → http://localhost:5134

# Rebuild cBot (after cBot code changes)
dotnet build src/TradingEngine.Adapters.CTrader --no-incremental
```
