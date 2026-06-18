# Shamshir Trading Engine — System Reference

**Written**: 2026-06-18 (updated post iter-31/32)
**Branch**: `iter/31-costs-journal`
**For**: Any implementing agent needing to understand the full system

---

## 1. What Is This System?

Shamshir is a **prop-firm algorithmic trading engine** targeting .NET 10 / C# 13. It runs
automated trading strategies with FTMO-style risk rules, position sizing, and drawdown
tracking. The engine is designed to be **strategy-agnostic** and **venue-agnostic** — the same
strategy code runs identically across backtest and live modes, with the venue differences
abstracted behind `IBrokerAdapter`.

### How it all fits together — end to end

```
MARKET DATA IN          →   EVALUATION    →   EXECUTION   →   TRACKING    →   PERSISTENCE
                                                                                │
  BacktestReplayAdapter      TradingLoop       OrderDispatcher   PositionTracker   PipelineEvents
  (bars from SQLite)    →    strategy.Evaluate  risk.Validate     OnExecutionAsync   + Trades
  SimulatedBrokerAdapter      EntryPlanner       broker.Submit     TradeCostCalc      + EquitySnapshots
  (ticks from CSV)            SignalGate         cost compute      exit reasons       + BarEvaluations
  cTrader cBot / NetMQ                                                        
                                                                                │
                                                                                ▼
STRATEGIES (4)           RISK GATE           VENUE FILLS        JOURNAL         WEB UI
  TrendBreakout          PROTECTION_MODE     market order       SIGNAL          Dashboard
  MeanReversion          DAILY_DD_LIMIT      limit (rest/expire) ORDER           Trades
  SessionBreakout        MAX_DD_LIMIT        cost computation   FILL            Report + Journal
  EMA Alignment          MAX_POSITIONS       SL/TP/force close  CLOSE           Live Monitor
                         MAX_EXPOSURE                            BREACH          SSE / SignalR
                         NEWS_WINDOW
```

**The pipeline per bar:**

1. **Data arrives** — bars come from the venue (SQLite replay, CSV synthesis, or live
   cTrader NetMQ). Each bar carries `(Symbol, Timeframe, OHLCV, OpenTimeUtc)`.

2. **Indicators are computed** — `IndicatorSnapshotService` maintains per-`(symbol, timeframe)`
   bar buffers and computes Skender indicators per strategy's `RequiredIndicators`. Keyed
   by full signature `(symbol, tf, type, period)` to prevent cross-strategy bleed.

3. **Strategies evaluate** — each active strategy gets a `MarketContext` (bars, indicators,
   current price, engine time) and returns an optional `TradeIntent`. Active strategies are
   filtered by `StrategyBankService` which respects `RunPlan` (per-run symbol/TF selection)
   and regime filters.

4. **Entry planning** — `EntryPlanner` reads the strategy's `OrderEntryOptions` and rewrites
   the intent's `OrderType` and `LimitPrice`. `Market` orders fill immediately. `LimitOffset`
   places a limit at a configurable pullback. SL/TP are re-derived so R stays consistent.

5. **Risk gate** — `RiskManager.Validate()` checks 8 violations (protection mode, daily DD,
   max DD, position caps, exposure, news, weekend). Also does worst-case portfolio projection
   and budget downsizing. Blocked signals journal as `REJECTED`.

6. **Order dispatched** — `OrderDispatcher` sizes the position (lot size = `floor(riskAmt /
   (slPips × pipValue) / lotStep) × lotStep`), applies drawdown scaling, and submits to the
   venue.

7. **Execution** — the venue fills the order and emits an `ExecutionEvent`. For market orders
   this happens immediately. For limit orders the venue rests the order until price reaches
   the limit or `LimitOrderExpiryBars` passes (then cancels → `ENTRY_EXPIRED`).

8. **Position tracking** — `PositionTracker` applies the execution event through
   `EngineReducer`, creating positions, tracking fills, and publishing `TradeClosed` with
   exit reason (SL/TP/FORCE/DailyDD/MaxDD).

9. **Cost computation** — `TradeCostCalculator` stamps every close with `GrossPnl`,
   `Commission` (round-turn × lots × perSide), `Swap` (nightsHeld × rate, triple on
   Wednesdays), and `NetPnl`. Both venues use the same calculator.

10. **Journal** — `PipelineEventWriter` persists every decision as a `PipelineEvent` with
    normalized `JournalEventKind` (SIGNAL → ORDER → FILL → CLOSE / REJECTED / BREACH /
    ENTRY_EXPIRED). The Report UI renders the full journal filterable by kind.

11. **Persistence** — three background handlers flush to SQLite: `TradePersistenceHandler`
    (trades), `BarEvaluationHandler` (per-bar strategy diagnostics), and
    `EquityPersistenceHandler` (equity snapshots for the curve).

**Multi-symbol / multi-timeframe:** The engine handles multiple symbols and timeframes
simultaneously. `RunPlan` (`(strategyId, symbol, timeframe)[]`) routes per-strategy selection.
Indicator snapshots are per-`(symbol, timeframe, strategy)`. Risk management is unified
across all symbols — `MAX_POSITIONS` and exposure caps are global.

**Config system:** DB is canonical. `StrategyConfigSeeder` populates from JSON on first run.
`IStrategyConfigStore` provides `GetAllAsync()` / `UpsertAsync()`. `EffectiveConfigResolver`
deep-merges stored defaults ← per-run overrides ← run plan for per-run customization.

**Strategies shipped:** TrendBreakout, MeanReversion (with `LimitOffset` demo), SessionBreakout,
EMA Alignment. All use ATR-based SL/TP, configurable position management (trailing,
breakeven), and regime filtering.

### Three venue paths

| Path | How | Used for | Credentials |
|------|-----|----------|-------------|
| **Replay** (default) | `BacktestReplayAdapter` reads bars from SQLite. Cost-aware fills via `TradeCostCalculator`. Supports limit orders. | Fast iteration, CI, development | None |
| **Synthetic** (testing) | `SimulatedBrokerAdapter` driven by `DataFeedService` + CSV. Tick-level fill simulation. | Simulation tests, harness-driven | None |
| **cTrader** (prod-equiv) | `ctrader-cli.exe` runs cBot in-process. NetMQ DEALER/ROUTER lock-step protocol. | Final verification | Required |

The default credential-free UI path (`RunEngineReplayAsync`) uses the Replay path when
`CTrader:UseForBacktest=false`.

### Architecture notes

**Kernel (EngineState/EngineReducer) is half-wired.** A pure functional kernel was built
across iter-20→23, but only the position-lifecycle slice is actively used:

| Component | Status | What's wired |
|-----------|--------|-------------|
| `EngineReducer.Apply` | **Partially wired** | Handles `OrderSubmitted/OrderFilled/OrderRejected/OrderCancelled/ForceCloseAll` from `PositionTracker`. Bar/time/equity events never reach it. |
| `PositionLifecycle` (FSM) | **Fully wired** | Position state transitions via the events above |
| `DrawdownReducer` | **Imperative only** | Called from `RiskManager.UpdateEquityLevels`/`OnDailyReset` — not through the reducer's `HandleEquityObserved` path |
| `GovernorMachine` | **Dead** | Never called. Governor runs via `TradingGovernorService` imperatively |
| `HandleBarClosed`/`DetectSlTpExit` | **Dead** | SL/TP exits simulated imperatively via `EngineRunner.SimulateBarExitsAsync` |
| `HandleDayRolled`/`WeekRolled`/`MonthRolled` | **Dead** | Published only to EventBus; reset logic runs in `RiskManager` |

**Known gap:** `TradingGovernorService.OnBar` (cooling-off decrement) is never called from
production. `TradingLoop` calls `signalGate?.OnBar()` which is `ISignalGate`, not
`ITradingGovernor`. Governor cooling-off state once entered may persist until daily reset.

Full analysis: `docs/archive/SYSTEM-MODEL.md`.

**Two config sources overlap** but are correctly reconciled: `RiskProfile` (strategy-level:
max DD%, risk%, exposure) and `PropFirmRuleSet` (FTMO guardrails: daily loss%, total loss%).
Both are checked; the prop firm rules are the hard floor.

**Fastest feedback loop**: `dotnet test tests/TradingEngine.Tests.Unit` (~2s, 207 tests).

---

## 2. Position Sizing — End-to-End

### Flow

```
Strategy.Evaluate() → TradeIntent
  → TradingLoop → EntryPlanner.Plan(intent, orderEntryOptions, signalPrice)
    → rewrites OrderType + LimitPrice + re-derived SL/TP per config
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
  → OrderCancelled (limit expiry)
    → PositionPhase.Cancelled → cleanup pending intent + dedup
    → journals as ENTRY_EXPIRED
```

### Position Lifecycle States

```
Active → BreakevenSet → Trailing → Closed | Cancelled (limit expiry)
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

### Risk Profiles (config/risk-profiles/)

| Profile | Risk/Trade | Max Daily DD | Max Total DD | Max SL | Max Exposure | DD Scale | Max Positions |
|---------|------------|--------------|--------------|--------|--------------|----------|---------------|
| Conservative | 0.5% | 3% | 6% | 50 pips | 3% | starts 50%, min 50% | 2 |
| Standard | 1.0% | 4% | 8% | 100 pips | 5% | starts 50%, min 50% | 3 |
| Aggressive | 2.0% | 5% | 10% | 150 pips | 10% | starts 75%, min 25% | 5 |

---

## 5. Multi-Symbol Support

The UI allows selecting up to 12 symbols and 6 timeframes. The `RunPlan` (`(strategyId, symbol, timeframe)[]`) routes per-strategy symbol/TF selection. `StrategyBankService.GetActive` filters strategies by run plan; falls back to strategy's stored defaults.

### Verified

Multi-symbol test (EURUSD + GBPUSD, H1): Unified risk management — `MAX_POSITIONS` violations block signals across both symbols.

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

Every strategy receives `MarketContext` with: symbol, latest tick, OHLC bars keyed by timeframe, pre-computed indicator values, engine time.

### TrendBreakout
- **Signal**: Break of 20-bar high (Long) or low (Short), confirmed above/below EMA50
- **SL/TP**: ATR-based (1.5× ATR offset), TP = 2.0× SL
- **Indicators**: EMA(50), ATR(14)

### MeanReversion
- **Signal**: RSI < 35 (oversold Long) or RSI > 65 (overbought Short), price within 0.2% of bar extremum
- **SL/TP**: ATR-based, TP = 1.0× SL
- **Indicators**: RSI(14), Bollinger Bands(20, 2.0σ), ATR(14)
- **Entry**: `LimitOffset` (demonstration) — places limit order at a pullback

### SessionBreakout
- **Signal**: Records 05:00-07:00 UTC session range, trades breakouts until 09:00 UTC
- **SL/TP**: ATR-based, TP = 2.0× SL
- **Flatten**: All positions closed at 12:00 UTC

### EMA Alignment
- **Signal**: Fast EMA(20) crosses above Slow EMA(50) (Long) or below (Short)
- **SL/TP**: ATR-based, TP = 2.0× SL

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
| `/backtests/run` | New backtest form — multi-symbol, timeframes, date picker, balance |
| `/backtest/{runId}` | Live progress via SSE — color-coded log, counters, result table |
| `/backtests/detail?runId=` | Completed backtest detail + per-strategy breakdown + **Journal tab** |
| `/events` | Last 100 engine events |

### API Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| `POST` | `/api/backtest/start` | Start backtest → `{runId, status}` |
| `GET` | `/api/backtest/{runId}/status` | Current status + result |
| `GET` | `/api/backtest/{runId}/logs` | Raw log lines |
| `GET` | `/api/backtest/{runId}/journal` | Paged journal with `?kind=&afterSeq=&limit=` |
| `GET` | `/api/backtest/{runId}/stream` | Server-Sent Events for live progress |

---

## 8. Cost & Journal System (iter-31)

### Cost Computation

`TradeCostCalculator` (in `Services/Helpers/`) is the single source of truth:

```
GrossProfit  = PipCalculator.GrossPnL(entry, exit, direction, lots, symbolInfo, crossRate)
Commission   = lots × commissionPerLotPerSide × 2  (round turn)
Swap         = nightsHeld × swapRate(direction) × lots (×3 on triple-swap day)
NetProfit    = GrossProfit − Commission − Swap
```

Cost data lives in `SymbolInfo` fields: `CommissionPerLotPerSide`, `SwapLongPerLotPerNight`, `SwapShortPerLotPerNight`, `TripleSwapWeekday`. All default to 0 (costs off). Seeded in `config/symbols.json` with realistic estimates.

### Journal

`JournalNormalizer` maps both live and persisted event vocabularies onto one taxonomy:

| Kind | Meaning |
|------|---------|
| SIGNAL | Strategy emitted TradeIntent (reason + direction + indicators) |
| ORDER | Order accepted by dispatcher (size, risk, profile) |
| FILL | Order filled / position opened |
| CLOSE | Position closed with exit reason + itemized costs |
| REJECTED | Order rejected by risk gate |
| BREACH | Drawdown limit breach detected |
| GOVERNOR | Governor state change |
| ENTRY_EXPIRED | Limit order expired unfilled |

### EntryPlanner (iter-31 C0)

Single place where order entry policy is applied. Called in `TradingLoop` after `strategy.Evaluate()`:

- Reads `OrderEntryOptions` from strategy config
- `Market` → immediate fill
- `LimitOffset` → places limit `offsetPips` more favorable, rests until price reached or expires
- `MarketWithSlippage` → immediate fill capped at `maxSlippagePips`
- Re-derives SL/TP off the planned entry so R stays consistent
- All strategies default to `Market`; `mean-reversion` uses `LimitOffset` as demonstration

---

## 9. Config System (iter-32)

### Source of Truth

DB is canonical. JSON is seed + manual export only. `StrategyConfigSeeder` seeds DB from `config/strategies/*.json` on first run (idempotent).

### Config Store

`IStrategyConfigStore` (EF-backed `SqliteStrategyConfigStore`) — `GetAllAsync()` / `UpsertAsync()`. `ConfigLoader` reads strategy configs from the store, not directly from JSON.

### Per-Run Overrides

`EffectiveConfigResolver` — deep-merges: stored default ← per-run overrides ← run plan. Pure, unit-tested.

### Run Plan

`RunPlan` record — `(strategyId, symbol, timeframe)[]`. Threaded through `StrategyBankService.GetActive()` to override strategy defaults per run.

---

## 10. Test Infrastructure

### Unit Tests: ~207 tests (credential-free)

Categories: RiskManager, DrawdownTracker, PositionSizer, PipCalculator, SlTpCalculator, TrailingStop, ExcursionTracker, ExitReason, DrawdownScaler, OrderDispatcher, PositionTracker, EntryPlanner, TradeCostCalculator, JournalNormalizer, EffectiveConfigResolver, EngineReducer, PositionLifecycle, StrategyBankService.

### Simulation Tests (credential-free)

| Test | Verifies |
|------|----------|
| FtmoGoldenJourney (4 tests) | FTMO rule enforcement over multi-bar scenarios |
| TradingLoopDirect | Strategy→EntryPlanner→OrderDispatcher integrated |
| DecisionJournal | Journal normalization + persistence |
| BacktestReplayCostsAndLimits | Cost computation + limit order fill/expiry |

### Test Harnesses

- `EngineTestHarness.cs` — In-process engine test builder (fluent API)
- `ReplayTestHarness.cs` — DB replay test builder
- `DrawdownTestHarness.cs` — Drawdown isolation test harness
- `AlwaysSignalStrategy.cs` — Test strategy that always fires signals

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
| `Interfaces/ISymbolInfoRegistry.cs` | Symbol metadata lookup (cost fields, etc.) |
| `Trading/TradeIntent.cs` | Signal-to-order bridge (Symbol, Direction, SL, TP, StrategyId, Reason, OrderType, LimitPrice) |
| `Trading/TradeResult.cs` | Completed trade (PnL, R-multiple, MAE/MFE, exit reason, Commission, Swap, Gross, Net) |
| `Trading/Position.cs` | Open position state |
| `RiskAndEquity/RiskProfile.cs` | Per-strategy risk configuration |
| `RiskAndEquity/PropFirmRuleSet.cs` | FTMO rule set |
| `MarketData/MarketContext.cs` | What strategies see (bars, indicators, tick, time) |
| `SymbolInfo/SymbolInfo.cs` | Symbol metadata (pip size, contract size, currencies, spread, cost fields) |

### Host (`src/TradingEngine.Host/`)
| File | Purpose |
|------|---------|
| `EngineWorker.cs` | Main engine loop (Backtest: single-threaded bar-stepped; Live: concurrent loops) |
| `EngineHostFactory.cs` | Single composition root — all DI, event handler wiring |
| `TradingLoop.cs` | Per-bar evaluate→plan→gate→dispatch pipeline (shared live+backtest) |
| `EngineHostOptions.cs` | Run configuration (RunId, Mode, AdapterFactory, DbPath, Symbols, RunPlan) |
| `ConfigLoader.cs` | Loads config from DB store (strategies) + JSON (risk/prop-firm/symbols) |
| `StrategyRegistry.cs` | Scans assembly for strategies, instantiates via config |
| `PipelineEventWriter.cs` | Channel-backed background flusher for pipeline journal |
| `TradePersistenceHandler.cs` | Background flusher for trade persistence |
| `EquityPersistenceHandler.cs` | Background flusher for equity snapshot persistence |
| `BarEvaluationHandler.cs` | Background flusher for bar evaluation persistence |
| `SymbolCatalog.cs` | Loads `config/symbols.json`, resolves symbol metadata incl. cost fields |
| `CrossRateStore.cs` | Mutable cross-rate store (GBP→USD, JPY→USD) |

### Services (`src/TradingEngine.Services/`)
| File | Purpose |
|------|---------|
| `PipCalculator.cs` | Distance, PipValue, GrossPnL, FloatingPnL |
| `PositionTracker.cs` | Tracks open positions, executes reducer, dispatches effects |
| `Helpers/TradeCostCalculator.cs` | Single source of truth for gross/commission/swap/net |
| `Helpers/EntryPlanner.cs` | Config→order type + limit price + re-derived SL/TP |
| `Helpers/JournalNormalizer.cs` | Maps event vocabularies to normalized taxonomy |
| `Helpers/EffectiveConfigResolver.cs` | Deep-merge default ← overrides ← run plan |

### Risk (`src/TradingEngine.Risk/`)
| File | Purpose |
|------|---------|
| `RiskManager.cs` | Central orchestrator — validates signals, calculates lot sizes, tracks positions |
| `DrawdownTracker.cs` | Tracks peak equity, daily/max drawdown fractions |
| `PositionSizer.cs` | Lot size calculation (5 methods) |
| `DrawdownScaler.cs` | Linear interpolation between 1.0 and scaleFloor |
| `Filters/NewsFilter.cs` | Stub — always returns false |
| `Filters/SessionFilter.cs` | Weekend detection |

### Infrastructure (`src/TradingEngine.Infrastructure/`)
| File | Purpose |
|------|---------|
| `Adapters/BacktestReplayAdapter.cs` | DB bar replay — instant-fills at close, cost-aware, limit order support |
| `Adapters/SimulatedBrokerAdapter.cs` | Tick-driven synthetic broker — cost-aware, limit order support |
| `Adapters/NetMQBrokerAdapter.cs` | NetMQ transport — SUB (telemetry), ROUTER (lock-step bar/command flow) |
| `Persistence/TradingDbContext.cs` | EF Core context — includes StrategyConfigs table |
| `Persistence/SqliteStrategyConfigStore.cs` | DB-backed strategy config store |
| `Persistence/StrategyConfigSeeder.cs` | Seeds DB from JSON on empty store |
| `Persistence/Repositories/` | SQLite-backed repositories |

### Web (`src/TradingEngine.Web/`)
| File | Purpose |
|------|---------|
| `Services/BacktestOrchestrator.cs` | Backtest lifecycle — start, run, cancel, status, trade stats |
| `Services/BacktestJournal.cs` | Unified SSE + log queue writer |
| `Api/BacktestController.cs` | REST endpoints (start, status, logs, journal, SSE stream) |
| `Pages/Backtests/Run.cshtml` | Multi-symbol backtest form |
| `Pages/Backtests/Progress.cshtml` | Live SSE progress |
| `Pages/Backtests/Report.cshtml` | Completed backtest detail + Journal tab |
| `Pages/Strategies.cshtml` | Strategy browse/edit (scaffolding) |

### Configuration (`config/`)
| File | Purpose |
|------|---------|
| `symbols.json` | 16 symbol definitions (pip size, contract size, currencies, spread, cost fields) |
| `prop-firms/ftmo-standard.json` | FTMO rules |
| `risk-profiles/` | Risk profile configs |
| `strategies/` | Strategy configs (seed → DB on first run) |
| `regime.json` | Regime detection config |
| `sizing-policy.json` | Sizing policy config |
| `governor.json` | Governor machine config |
| `rotation.json` | Strategy rotation config |

---

## 12. Lock-Step Protocol (cTrader path only)

The cBot and engine communicate via a deterministic lock-step protocol over NetMQ DEALER/ROUTER:

```
cBot → engine   {type:"hello", v:1, symbols:[..], periods:[..], barsLoaded:N}
engine → cBot   {type:"hello_ack", v:1}

cBot → engine   {type:"bar", seq:N, symbol, period, openTime, o,h,l,c, volume, simTime, account}
cBot BLOCKS until:
engine → cBot   {type:"bar_done", seq:N, commands:[{submit_order,...},{close_position,...}]}
cBot executes commands synchronously at simulated time:
cBot → engine   {type:"bar_result", seq:N, execs:[{clientOrderId,kind,state,fillPrice,...}], account}

cBot → engine   {type:"exec", ...}     ← async SL/TP hits between bars

cBot → engine   {type:"stats", barsSent:N, cmdsReceived:N, ordersExecuted:N, execsSent:N}
engine → cBot   {type:"shutdown"}
```

Full protocol spec: `docs/iterations/iter-17/PROTOCOL.md`

---

## 13. Build and Test Commands

```powershell
# Full build
dotnet build

# Unit tests (~207 pass)
dotnet test tests/TradingEngine.Tests.Unit

# Simulation tests (FTMO + replay)
dotnet test tests/TradingEngine.Tests.Simulation

# Architecture tests
dotnet test tests/TradingEngine.Tests.Architecture

# Run web UI
dotnet run --project src/TradingEngine.Web

# EF migration
dotnet ef migrations add <Name> --startup-project src/TradingEngine.Web --project src/TradingEngine.Infrastructure
```

---

## 14. Key Design Decisions (summary)

See `DECISIONS.md` in repo root for full decision registry (D1–D80).

| Decision | Value |
|----------|-------|
| Money math | `decimal` for all price/money/lot arithmetic |
| Lot rounding | `Math.Floor`, never `Math.Round` |
| Default venue | `BacktestReplayAdapter` (credential-free, cost-aware) |
| Cost source | `TradeCostCalculator` — single source, used by both venues |
| Order entry | `EntryPlanner` — one place, config-driven, not per-strategy |
| Config source | DB canonical (JSON = seed + export only) |
| Journal taxonomy | `JournalNormalizer` maps both vocabularies to `SIGNAL/ORDER/FILL/CLOSE/...` |
| Schema | EF migrations only — no raw SQL |
| Logging | Serilog only — no `Console.WriteLine` |
| Time | `IEngineClock` — never `DateTime.UtcNow` directly |
| Channels | `Wait` for order/trade; `DropOldest` only for analytics |
