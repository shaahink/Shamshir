# Shamshir — System & Entity Model

> Combined architecture + data model reference for LLM analysis.
> Covers: how the engine works, what entities exist, how data flows,
> when replay vs cTrader is used, and where everything lives.

---

## 1. System Architecture

### 1.1 What Is This System?

Shamshir is a **prop-firm algorithmic trading engine** targeting .NET 10 / C# 13.
It runs automated trading strategies with FTMO-style risk rules, position sizing,
and drawdown tracking. The engine is **strategy-agnostic** and **venue-agnostic** —
the same strategy code runs identically across backtest and live modes.

### 1.2 The Kernel Architecture (post iter-36)

After the kernel cutover, there is **one engine** with a pure functional kernel.
The old imperative `TradingLoop`/`OrderDispatcher`/`AccountProcessor` live only
in `tests/TradingEngine.Tests.Support` as the golden regression oracle (D81).

```
┌─────────────────────────────────────────────────────────────────┐
│                    KernelBacktestLoop (Host)                     │
│                                                                 │
│  Market Data → BarEvaluator → OrderProposed events              │
│                                    │                            │
│                                    ▼                            │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │              Kernel (pure decision core)                   │   │
│  │  PreTradeGate (8-step risk walk)                          │   │
│  │  KernelSizing (5 lot-sizing methods)                      │   │
│  │  EngineReducer (15 state transition handlers)             │   │
│  │  DrawdownReducer (DD math)                                │   │
│  │  GovernorMachine (session risk)                           │   │
│  │  PositionLifecycle (FSM)                                  │   │
│  └──────────────────────────────────────────────────────────┘   │
│        │  EngineDecision (new EngineState + EngineEffect[])     │
│        ▼                                                        │
│  EffectExecutor → IBrokerAdapter (venue)                        │
│        │                                                        │
│        ▼                                                        │
│  Venue feedback → KernelFeedback → EngineEvents                 │
│        │                                                        │
│        ▼                                                        │
│  ChannelJournalWriter → StepRecord stream (lossless)            │
└─────────────────────────────────────────────────────────────────┘
```

**Every state transition**: `(EngineState, EngineEvent) → (new EngineState, EngineEffect[])`
**Every step journaled**: `StepRecord` with event JSON, effects, risk snapshot, strategy verdicts.

### 1.3 When Replay vs cTrader

| Decision Point | Replay (BacktestReplayAdapter) | cTrader (NetMQBrokerAdapter) |
|---|---|---|
| **When used** | Default credential-free path. `appsettings` → `CTrader:UseForBacktest=false` | When `CTrader:UseForBacktest=true`, or in Development profile |
| **Bar source** | SQLite `Bars` table (pre-seeded) | cBot via NetMQ lock-step protocol |
| **Fill model** | Bar-close (market) or limit-price-reached | cTrader backtester engine |
| **Cost computation** | `TradeCostCalculator` stamps `GrossPnL`, `Commission`, `Swap`, `NetPnL` | cBot computes costs (then mapped by adapter) |
| **Determinism** | Fully deterministic (same data + config = same output) | Non-deterministic (cTrader engine) |
| **Credentials** | None required | `CTrader:CtId`, `CTrader:PwdFile`, `CTrader:Account` required |
| **Where tested** | Unit + Simulation (cred-free) | cTrader E2E tests (credential-gated) |
| **UI venue selector** | `"replay"` or `""` (default) | `"ctrader"` |
| **API field** | `StartRunRequest.Venue = null` or `"replay"` | `StartRunRequest.Venue = "ctrader"` |

### 1.4 Project Layout

```
src/
  TradingEngine.Domain/           # Pure domain: value objects, interfaces, events, kernel core
  TradingEngine.Engine/           # Kernel, PreTradeGate, KernelSizing, EngineReducer, DrawdownReducer
  TradingEngine.Application/      # Assembly marker only
  TradingEngine.Infrastructure/   # EF Core, Skender, adapters, persistence, NetMQ transport
  TradingEngine.Risk/             # RiskManager, DrawdownTracker, PositionSizer (test-oracle only now)
  TradingEngine.Strategies/       # 9 strategy implementations
  TradingEngine.Services/         # PipCalc, SL/TP, trailing, EntryPlanner, TradeCost, AddOnResolver
  TradingEngine.Host/             # EngineRunner, KernelBacktestLoop, BarEvaluator, EffectExecutor
  TradingEngine.Web/              # ASP.NET API controllers, Razor shell for Angular SPA
  TradingEngine.Adapters.CTrader/ # C# 6 cBot (net6.0)
tests/
  TradingEngine.Tests.Unit/       # xUnit isolated tests (~267 pass)
  TradingEngine.Tests.Integration/# DI + SQLite + WebApplicationFactory (~67 pass)
  TradingEngine.Tests.Simulation/ # Full engine end-to-end (~82 pass credential-free)
  TradingEngine.Tests.Support/    # Golden oracle (OrderDispatcher, KernelOrderGate, AccountProcessor)
```

---

## 2. Entity Catalog

### 2.1 DB Entities (17 tables in TradingDbContext)

#### `BacktestRunEntity` (table: `BacktestRuns`)
| Field | Type | Purpose |
|-------|------|---------|
| `RunId` | string PK | 8-char hex run identifier |
| `StartedAtUtc` | DateTime | When the run was started |
| `CompletedAtUtc` | DateTime | When the run finished |
| `Symbol` / `Period` | string | Legacy single symbol/timeframe |
| `Symbols` / `Periods` | string (JSON) | Multi-symbol/timeframe arrays |
| `BacktestFrom` / `BacktestTo` | DateTime | Date range |
| `InitialBalance` | decimal | Starting balance |
| `AlgoHash` | string | Version hash |
| `StrategyParamsJson` | string | Strategy configuration JSON |
| `EffectiveConfigJson` | string? | Resolved per-run config |
| `NetProfit` | decimal | Net PnL |
| `GrossPnL` | decimal | Gross PnL |
| `CommissionTotal` | decimal | Total commission |
| `SwapTotal` | decimal | Total swap |
| `MaxDrawdownPct` | decimal | Maximum drawdown % |
| `TotalTrades` | int | Trade count |
| `WinningTrades` | int | Win count |
| `WinRatePct` | double | Win rate % |
| `ExitCode` | int | 0 = success |
| `ErrorMessage` | string? | Error if failed |
| `DatasetId` | string? | Dataset identity hash |
| `ConfigSetId` | string? | Config identity hash |
| `Seed` | int | Deterministic seed |
| `ParentRunId` | string? | Lineage parent run |

#### `TradeResultEntity` (table: `TradeResults`)
| Field | Type | Purpose |
|-------|------|---------|
| `Id` | Guid PK | Trade ID |
| `PositionId` | Guid | Position ID |
| `OrderId` | Guid | Client order ID (reconciliation join) |
| `Symbol` | string | Instrument |
| `Direction` | string | Long / Short |
| `Lots` | decimal | Position size |
| `EntryPrice` | decimal | Entry price |
| `ExitPrice` | decimal | Exit price |
| `StopLoss` | decimal | Stop loss price |
| `TakeProfit` | decimal? | Take profit price |
| `OpenedAtUtc` | DateTime | Entry time |
| `ClosedAtUtc` | DateTime | Exit time |
| `GrossPnLAmount` / `GrossPnLCurrency` | decimal/string | Gross PnL |
| `CommissionAmount` / `CommissionCurrency` | decimal/string | Commission |
| `SwapAmount` / `SwapCurrency` | decimal/string | Swap |
| `NetPnLAmount` / `NetPnLCurrency` | decimal/string | Net PnL |
| `PnLPips` | double | PnL in pips |
| `RMultiple` | double | R-multiple |
| `MaxAdverseExcursion` | double | MAE in pips |
| `MaxFavorableExcursion` | double | MFE in pips |
| `ExitReason` | string | SL / TP / FORCE / DailyDD / MaxDD |
| `StrategyId` | string | Strategy that generated the trade |
| `RiskProfileId` | string | Risk profile used |
| `Mode` | string | Backtest / Paper / Live |
| `DurationSeconds` | double | Hold time |
| `RunId` | string? | Run identifier |

Indexes: `ClosedAtUtc`, `StrategyId`

#### `JournalEntryEntity` (table: `JournalEntries`)
| Field | Type | Purpose |
|-------|------|---------|
| `RunId` + `Seq` | string+long (composite PK) | Run + monotonic sequence |
| `SimTimeUtc` | DateTime | Simulation time |
| `EventKind` | string | Event kind for display |
| `EventJson` | string | Full event JSON |
| `EffectKinds` | string (JSON array) | Effect types produced |
| `EffectsJson` | string (JSON array) | Full effects JSON |
| `RiskJson` | string (JSON) | Risk snapshot at decision time |
| `StrategyVerdicts` | string (JSON array) | Per-strategy verdicts |
| `Regime` | string? | Market regime |
| `DecisionReason` | string? | Reason for decision |

Composite index: `(RunId, SimTimeUtc)`

#### `BarEntity` (table: `Bars`)
`RunId`, `Symbol`, `Timeframe`, `OpenTimeUtc`, `Open`, `High`, `Low`, `Close`, `Volume`
Index: `(RunId, Symbol, Timeframe, OpenTimeUtc)`

#### `EquitySnapshotEntity` (table: `EquitySnapshots`)
`TimestampUtc`, `Balance`, `FloatingPnL`, `Equity`, `PeakEquity`, `DailyStartEquity`, `CurrentDailyDrawdown`, `CurrentMaxDrawdown`, `Mode`, `Type` (default "Tick"), `RunId`
Indexes: `TimestampUtc`, `RunId`

#### `StrategyConfigEntity` (table: `StrategyConfigs`)
`Id` PK, `DisplayName`, `Enabled`, `DefaultSymbols` (JSON), `Timeframe`, `RiskProfileId`, `ParametersJson`, `PositionManagementJson`, `OrderEntryJson`, `RegimeFilterJson`, `ReentryJson`

#### `RiskProfileEntity` (table: `RiskProfiles`)
`Id` PK, `DisplayName`, `Json` (blob of full RiskProfile)

#### `PropFirmRuleSetEntity` (table: `PropFirmRuleSets`)
`Id` PK, `DisplayName`, `Json` (blob of full PropFirmRuleSet)

#### `GovernorOptionsEntity` (table: `GovernorOptions`)
`Id` PK (default "default"), `Json` (blob of full GovernorOptions)

#### `AddOnPackEntity` (table: `AddOnPacks`)
`Id` PK, `Name`, `Description?`, `AddOnsJson` (JSON of PositionManagementOptions), `RegimeDetectionEnabled`

#### `DatasetEntity` (table: `Datasets`)
`Id` PK, `ContentHash`, `Symbols` (JSON), `Timeframes` (JSON), `FromUtc`, `ToUtc`, `Granularity`, `RowCount`
Index: `ContentHash`

#### `ConfigSetEntity` (table: `ConfigSets`)
`Id` PK, `ContentHash`, `Json` (JSON blob)
Index: `ContentHash`

#### `ExperimentEntity` (table: `Experiments`)
`Id` PK, `Name`, `Hypothesis`, `SpecJson`, `Status` (default "Pending")
Navigation: `Runs → ICollection<ExperimentRunEntity>`

#### `ExperimentRunEntity` (table: `ExperimentRuns`)
`Id` PK, `ExperimentId` FK, `BacktestRunId`, `VariantLabel`, `FoldIndex`, `FoldRole` (default "Test"), `ScoreJson`

#### `OrderEntity` (table: `Orders`)
`Id` PK, `Symbol`, `Direction`, `OrderType`, `State`, `RequestedLots`, `FillPrice?`, `FilledLots`, `RejectionReason?`, `LimitPrice?`, `StopLoss?`, `TakeProfit?`, `StrategyId`, `RiskProfileId`, `Reason`

#### `PositionEntity` (table: `Positions`)
`Id` PK, `OrderId`, `Symbol`, `Direction`, `Lots`, `EntryPrice`, `CurrentStopLoss`, `TakeProfit?`, `OpenedAtUtc`, `ClosedAtUtc?`, `StrategyId`, `ExitReason?`

#### `EngineEventEntity` (table: `EngineEvents`)
`Id` PK, `EventType`, `Payload` (JSON), `OccurredAtUtc`

All entities implement `IAuditableEntity`: `CreatedAtUtc`, `UpdatedAtUtc` (auto-stamped by `AuditStampInterceptor`).

---

### 2.2 Domain Value Objects

| Type | Kind | Fields | Notes |
|------|------|--------|-------|
| `Symbol` | `readonly record struct` | `string Value` | Instrument identifier |
| `Price` | `readonly record struct` | `decimal Value` | Price in instrument-specific units |
| `Pips` | `readonly record struct` | `double Value` | Pip distance |
| `Money` | `readonly record struct` | `decimal Amount, string Currency` | PnL, costs |
| `RiskPercent` | `readonly record struct` | `double Value` | 0..1 exclusive |

---

### 2.3 Core Domain Types

#### `TradeIntent` — what a strategy emits
| Field | Type | Purpose |
|-------|------|---------|
| `Symbol` | Symbol | Instrument |
| `Direction` | TradeDirection | Long / Short |
| `OrderType` | OrderType | Market / Limit / Stop |
| `LimitPrice` | Price? | Limit order price |
| `StopLoss` | Price | Stop loss price |
| `TakeProfit` | Price? | Take profit price |
| `StrategyId` | string | Which strategy |
| `RiskProfileId` | string | Which risk profile |
| `Reason` | string | Signal description |
| `CreatedAtUtc` | DateTime | When generated |
| `Entry` | OrderEntryOptions? | Entry configuration |

#### `TradeResult` — completed trade
| Field | Type | Purpose |
|-------|------|---------|
| `Id` | Guid | Trade identifier |
| `PositionId` | Guid | Position identifier |
| `OrderId` | Guid | Venue order id |
| `Symbol` | Symbol | Instrument |
| `Direction` | TradeDirection | Long/Short |
| `Lots` | decimal | Position size |
| `EntryPrice` | Price | Entry price |
| `ExitPrice` | Price | Exit price |
| `StopLoss` | Price | Stop loss |
| `TakeProfit` | Price? | Take profit |
| `OpenedAtUtc` | DateTime | Entry time |
| `ClosedAtUtc` | DateTime | Exit time |
| `GrossPnL` / `Commission` / `Swap` / `NetPnL` | Money | Itemized costs |
| `PnLPips` | Pips | PnL in pips |
| `RMultiple` | double | R-multiple |
| `MaxAdverseExcursion` | Pips | MAE |
| `MaxFavorableExcursion` | Pips | MFE |
| `ExitReason` | string | SL/TP/FORCE/DailyDD/MaxDD |
| `StrategyId` | string | Strategy |
| `RiskProfileId` | string | Risk profile |
| `DurationSeconds` | double | Hold time (computed) |
| `Timeframe` | string? | Run timeframe |

#### `MarketContext` — what strategies see
| Field | Type | Purpose |
|-------|------|---------|
| `Symbol` | Symbol | Instrument |
| `LatestTick` | Tick | Current bid/ask |
| `Bars` | `IReadOnlyDictionary<Timeframe, IReadOnlyList<Bar>>` | OHLC bars by timeframe |
| `IndicatorValues` | `IReadOnlyDictionary<string, double>` | Pre-computed indicators (bare key, no symbol prefix) |
| `EngineTimeUtc` | DateTime | Current engine time |

#### `Bar` — OHLC candle
| Field | Type |
|-------|------|
| `Symbol` | Symbol |
| `Timeframe` | Timeframe |
| `OpenTimeUtc` | DateTime |
| `Open` / `High` / `Low` / `Close` | decimal |
| `Volume` | double |

---

### 2.4 Kernel State Types

#### `EngineState` — authoritative, immutable state
| Field | Type | Purpose |
|-------|------|---------|
| `Positions` | `IReadOnlyDictionary<Guid, PositionState>` | All open positions |
| `Governor` | `GovernorState` | Governor trading state |
| `Drawdown` | `DrawdownState` | Drawdown tracking |
| `OpenPositionCount` | int | Open position count |
| `Protection` | `ProtectionState` | Protection mode status |
| `Account` | `AccountView` | Balance, Equity, FloatingPnL |

#### `DrawdownState` — drawdown math
| Field | Type |
|-------|------|
| `InitialAccountBalance` | decimal |
| `PeakEquity` | decimal |
| `DailyStartEquity` | decimal |
| `WeeklyStartEquity` | decimal |
| `MonthlyStartEquity` | decimal |
| `CurrentDailyDrawdown` | decimal |
| `CurrentMaxDrawdown` | decimal |
| `CurrentWeeklyDrawdown` | decimal |
| `CurrentMonthlyDrawdown` | decimal |
| `DrawdownVelocity` | decimal |
| `DrawdownType` | string (Fixed / Trailing) |
| `DailyDdBaseMode` | string (InitialBalance / DailyStart) |

#### `ProtectionState`
`InProtectionMode` (bool), `Cause` (ProtectionCause: None/DailyDrawdown/MaxDrawdown/WeeklyDrawdown/MonthlyDrawdown), `Reason` (string?), `ResetPolicy` (string), `UntilUtc` (DateTime?)

#### `GovernorState`
`State` (Normal/Reduced/SoftStop/CoolingOff/ProfitLocked/HardStop), `ConsecutiveLosses`, `CoolingOffBarsRemaining`, `DayNetPnLFraction`, `LastSizeMultiplier`, `ProfitLockedToday`, `Reason`

#### `AccountView`
`Balance`, `Equity`, `FloatingPnL` (all decimal)

#### `PositionState`
`PositionId`, `OrderId`, `Symbol`, `Direction`, `Lots`, `EntryPrice`, `CurrentStopLoss`, `TakeProfit?`, `OpenedAtUtc`, `StrategyId`, `Phase` (Intended/Submitted/Open/Reducing/Closing/Closed/Rejected/Cancelled), `HighWater`, `LowWater`, `BreakevenApplied`, `InitialSlDistance`, `CloseReason?`

#### `ConstraintSet` — kernel config
| Field | Type |
|-------|------|
| `MaxDailyLoss` / `MaxTotalLoss` / `MaxWeeklyLoss` / `MaxMonthlyLoss` | decimal |
| `ProfitTarget` | decimal |
| `DrawdownType` | string |
| `DailyDdBase` | DailyDdBase enum |
| `RiskPerTrade` | decimal |
| `MaxConcurrentPositions` | int |
| `MaxExposure` | decimal |
| `AllowTradesDuringNews` | bool |
| `AllowWeekendHolding` | bool |
| `ForceCloseOnBreach` | bool |
| 9 ProtectionToggles | bool each |

---

### 2.5 Engine Events (15 types)

| Event | Fields | When Emitted |
|-------|--------|-------------|
| `BarClosed` | Symbol, Timeframe, OHLC, BarOpenTimeUtc | Each bar |
| `BarIngested` | RunId, Bar | Per-bar persistence |
| `TickReceived` | Symbol, Bid, Ask | Tick feed |
| `OrderProposed` | OrderId, Symbol, Direction, OrderType, LimitPrice?, SL, TP?, StrategyId, SignalPriceMid, SlPips, PipValuePerLot, External (verdicts), Profile | Strategy signal |
| `OrderSubmitted` | OrderId, Symbol, Direction, Lots, LimitPrice?, StrategyId, SL, TP? | Kernel accepted |
| `OrderFilled` | OrderId, Symbol, FilledLots, FillPrice, GrossProfit?, NetProfit?, Commission?, Swap? | Venue filled |
| `OrderPartiallyFilled` | OrderId, Symbol, FilledLots, FillPrice | Venue partial fill |
| `OrderRejected` | OrderId, Symbol, Reason | Venue rejected |
| `OrderCancelled` | OrderId, Symbol, Reason | Limit expired |
| `CloseRequested` | PositionId, Reason | Intent to close |
| `StopLossModifyRequested` | PositionId, NewStopLoss, Kind | Trail/BE |
| `PartialCloseRequested` | PositionId, CloseLots, Reason | Partial take-profit |
| `AddOnsResolved` | PositionId, DetailJson | Add-on values frozen |
| `EquityObserved` | Balance, Equity, FloatingPnL | Account update |
| `DayRolled` / `WeekRolled` / `MonthRolled` | OccurredAtUtc only | Time boundary |

### 2.6 Engine Effects (10 types)

| Effect | Purpose | Venue Action |
|--------|---------|-------------|
| `SubmitOrder` | Order to venue | `broker.SubmitOrderAsync` |
| `ModifyStopLoss` | Modify SL | `broker.ModifyOrderAsync` |
| `ModifyTakeProfit` | Modify TP | `broker.ModifyOrderAsync` |
| `CloseOpenPosition` | Close full position | `broker.ClosePositionAsync` |
| `ClosePartialOpenPosition` | Close partial position | `broker.ClosePartialPositionAsync` |
| `PublishTradeClosed` | Create TradeResult | Domain event |
| `RegisterRisk` | Register with risk manager | `riskManager.RegisterPosition` |
| `DeregisterRisk` | Deregister from risk manager | `riskManager.DeregisterPosition` |
| `RecordDecisionEvent` | Journal-only event | Golden oracle only |

---

### 2.7 API DTOs (key types)

#### `StartRunRequest` (POST /api/runs)
| Field | Type | Default | Where Displayed |
|-------|------|---------|-----------------|
| `Symbols` / `Periods` | List<string> | Multi-select chips on New Backtest form | Form |
| `Start` / `End` | DateTime | Date picker, quick-select buttons | Form |
| `Balance` | decimal | 100,000 | Form |
| `CommissionPerMillion` | double | 30 | Form |
| `SpreadPips` | double | 1 | Form |
| `StrategyIds` | List<string> | Strategy cards (pre-selected all enabled) | Form |
| `RiskProfileId` | string | Dropdown from GET /api/risk-profiles | Form |
| `Venue` | string? | Dropdown: "" or "replay" or "ctrader" | Form |
| `StrategyOverrides` | Dictionary<string, Dictionary> | Per-strategy JSON textareas | Form |
| `UsePackId` | string? | Add-on pack dropdown | Form |
| `DisableRegime` | bool | Checkbox | Form |
| `PerStrategyPackIds` | Dictionary<string,string> | Per-strategy pack overrides | Form |

#### `RunSummary` (GET /api/runs — list page)
`runId`, `status`, `symbol`/`symbols`, `period`/`periods`, `startedAtUtc`, `completedAtUtc`, `netProfit`, `grossPnL`, `commissionTotal`, `swapTotal`, `maxDrawdownPct`, `totalTrades`, `winningTrades`, `winRatePct`, `parentRunId?`, `datasetId?`, `configSetId?`

#### `RunDetail` (GET /api/runs/{id} — report page)
Extends `RunSummary` with: `backtestFrom`, `backtestTo`, `initialBalance`, `exitCode`, `effectiveConfigJson`

#### `TradeSummary` (GET /api/runs/{id}/trades — trades table)
18 columns: Sym, Dir, Lots, Entry, Exit, Gross, Comm, Swap, Net, Pips, R, MAE, MFE, ExitReason, StrategyId, DurationSec, EntryType, Timeframe

#### `JournalEntry` (GET /api/runs/{id}/journal — journal viewer)
`seq`, `simTimeUtc`, `eventKind`, `eventJson`, `effectKinds`, `risk` (RiskSnapshot), `strategyVerdicts[]`, `decisionReason`, `kind`, `symbol`, `strategyId`, `reason`, `detail`

#### `EquityPoint` (GET /api/runs/{id}/equity — equity chart)
`timestampUtc`, `equity`, `balance`

#### `StrategyPerformance` (GET /api/runs/{id}/analytics/strategies)
`strategyId`, `totalBarsEvaluated`, `signalsFired`, `tradesOpened`, `wins`, `losses`, `winRatePct`, `topRejections[]`

---

### 2.8 All Enums

| Enum | Values | Used In |
|------|--------|---------|
| `TradeDirection` | Long, Short | Every trade signal |
| `OrderType` | Market, Limit, Stop | TradeIntent |
| `EngineMode` | Backtest, Paper, Live | Run context |
| `Timeframe` | M1, M5, M15, M30, H1, H4, D1, W1 | Bar data |
| `MarketRegime` | Unknown, Trending, Ranging, HighVolatility, LowVolatility | Strategy filter |
| `LotSizingMethod` | PercentRisk, FixedLots, FixedDollarRisk, KellyFraction, AntiMartingale | RiskProfile |
| `DailyDdBase` | InitialBalance, DailyStart | PropFirmRuleSet |
| `OrderEntryMethod` | Market, LimitOffset, MarketWithSlippage | OrderEntryOptions |
| `OrderState` | Created, Submitted, Accepted, PartiallyFilled, Filled, Cancelled, Rejected | Order tracking |
| `PositionPhase` | Intended, Submitted, Open, Reducing, Closing, Closed, Rejected, Cancelled | PositionLifecycle |
| `TrailingMethod` | StepPips, AtrMultiple, BreakevenThenTrail, Structure, SteppedR, None | TrailingOptions |
| `SlMethod` | FixedPips, AtrMultiple, SwingBased | SlOptions |
| `TpMethod` | None, FixedPips, RRMultiple, AtrMultiple | TpOptions |
| `AddOnMode` | Auto, Custom | All add-on options |
| `ProtectionCause` | None, DailyDrawdown, MaxDrawdown, WeeklyDrawdown, MonthlyDrawdown | ProtectionState |
| `GovernorTradingState` | Normal, Reduced, SoftStop, CoolingOff, ProfitLocked, HardStop | GovernorState |
| `IndicatorType` | Ema, Sma, Rsi, Atr, BollingerBands, Macd, Adx, SuperTrend | IndicatorRequest |
| `RotationMode` | Disabled, PerformanceBased, RegimeBased, Combined | StrategyRotation |

---

## 3. Strategy System

### 3.1 IStrategy Interface
```csharp
public interface IStrategy {
    string Id { get; }
    string DisplayName { get; }
    IStrategyConfig Config { get; }
    IReadOnlyList<Timeframe> RequiredTimeframes { get; }
    int RequiredBarCount { get; }
    IReadOnlyList<IndicatorRequest> RequiredIndicators { get; }
    TradeIntent? Evaluate(MarketContext context);  // Generate signal
    void OnTradeResult(TradeResult result);         // Feedback
    void Reset();                                   // Clear state
}
```

### 3.2 9 Strategies

| # | ID | Signal Logic | Required Indicators | Key Parameters |
|---|----|-------------|---------------------|----------------|
| 1 | `trend-breakout` | Break of 20-bar high/low + EMA50 filter | ATR(14), EMA(50) | `lookbackBars=20`, `maPeriod=50` |
| 2 | `ema-alignment` | Fast EMA(20) > Slow EMA(50) + price above fast | EMA(20), EMA(50), ATR(14) | `fastPeriod=20`, `slowPeriod=50` |
| 3 | `mean-reversion` | RSI oversold/overbought + near bar extreme | RSI(14), BB(20,2), ATR(14) | `rsiOversold=30`, `rsiOverbought=70`, `proximityToExtremeFraction=0.33` |
| 4 | `session-breakout` | Session range (05-07UTC) breakout, flatten 12UTC | ATR(14) | `rangeStartUtc`, `rangeEndUtc`, `entryWindowEndUtc`, `flattenTimeUtc` |
| 5 | `super-trend` | SuperTrend flip + ADX > 20 | SuperTrend, ADX(14), ATR(10) | `atrPeriod=10`, `atrMultiplier=3.0` |
| 6 | `rsi-divergence` | Bullish/bearish RSI divergence vs price | RSI(14), ATR(14) | `rsiPeriod=14`, `divergenceLookback=10` |
| 7 | `mtf-trend` | RSI cross on H1 aligned with H4 EMA200 | RSI(14)[H1], EMA(200)[H4], ATR(14) | `rsiBullishPullback=45`, `rsiBearishPullback=55` |
| 8 | `macd-momentum` | MACD histogram zero cross + SMA + ADX | MACD(12,26,9), SMA(200), ADX(14), ATR(14) | `macdFast=12`, `macdSlow=26`, `macdSignal=9` |
| 9 | `bb-squeeze` | BB width contraction → latch squeeze → band breakout | BB(20,2), ATR(14) | `squeezeThreshold=0.8`, `cooldownBars=3` |

### 3.3 Config Storage

**Source of truth**: DB (StrategyConfigs table). JSON is seed + export only.

**Flow**:
1. `config/strategies/*.json` (9 files)
2. → `StrategyConfigSeeder` seeds DB on first run (idempotent)
3. → `StrategyConfigEntity` stored in SQLite
4. → `ConfigLoader` loads from `IStrategyConfigStore` (DB)
5. → `EffectiveConfigResolver` deep-merges per-run overrides
6. → Strategies instantiated with final config

**Config sections per strategy**:
- `id`, `displayName`, `enabled`
- `symbols[]` — which instruments
- `timeframe` — primary timeframe (e.g. "H1")
- `riskProfileId` — references a RiskProfile
- `regimeFilter` — 5 booleans: allow Trending/Ranging/HighVol/LowVol/Unknown
- `orderEntry` — `{method, maxSlippagePips, limitOffsetPips?, limitOrderExpiryBars?}`
- `positionManagement` — baseline SL/TP + 5 add-ons
- `parameters` — strategy-specific params (varies per strategy)
- `reentry` — cooldown bars after SL/TP/entry

### 3.4 Per-Run Customization

`EffectiveConfigResolver` deep-merges:
1. Stored default (DB)
2. Per-run overrides (`StartRunRequest.strategyOverrides`)
3. Run Plan (`(strategyId, symbol, timeframe)[]` — routes per-strategy symbol/TF)

`ConfigSetId` hash captures the exact config for K6 determinism.

---

## 4. Add-On System

### 4.1 Five Add-On Types

| Add-On | Record | Enabled Fields | Auto-Tune Behavior |
|--------|--------|---------------|-------------------|
| **Breakeven** | `BreakevenOptions` | `Enabled`, `Mode`, `TriggerRMultiple` (1.0), `OffsetPips` (1.0) | triggerR = 1.0/volFactor, offsetPips = ceil(spread*1.5)+1 |
| **Trailing** | `TrailingOptions` | `Enabled`, `Mode`, `Method`, `StepPips`, `AtrMultiple`, `ActivateAfterBreakeven` | atrMultiple = tfBase*volFactor, stepPips = 0.5*atrPips |
| **PartialTp** | `PartialTpOptions` | `Enabled`, `Mode`, `TriggerRMultiple` (1.0), `CloseFraction` (0.5) | triggerR = 1.0, closeFraction = 0.5 |
| **Ride** | `RideOptions` | `Enabled`, `Mode`, `AdxFloor` (25), `RelaxedAtrMultiple` (3.0) | adxFloor = 25, relaxedAtr = trailAtr*1.4 |
| **DynamicSlTp** | `DynamicSlTpOptions` | `Enabled`, `Mode`, `AtrMultipleSl` (1.5), `RrMultipleTp` (2.0) | slAtr = 0.8*tfBase, tpRr = 1.5+0.25*tfTier |

All in `AddOnMode.Auto` by default — numbers computed at entry via `AddOnAutoTuner`.
`AddOnMode.Custom` uses stored values verbatim.

### 4.2 Add-On Packs

3 seeded packs in `AddOnPacks` table:
- `breakeven-only` — Breakeven (Auto)
- `scalp-tight` — Breakeven + Trailing StepPips (Auto)
- `runner-aggressive` — Breakeven + Trailing AtrMultiple + Ride + PartialTp (Auto)

Pack add-ons **replace** strategy's add-ons for a run. Baseline SL/TP always from strategy.
Selected via `StartRunRequest.usePackId` or `perStrategyPackIds`.

### 4.3 Auto-Tuner (`AddOnAutoTuner`)

Pure, deterministic. Input: `(timeframe, atrPips, spreadPips, referenceAtrPips)`.
```
tfBase = TrailingBaseFor(tf)  // M1/M5/M15→2.0, M30/H1→2.5, H4→3.0, D1/W1→3.5
volFactor = Clamp(atrPips / refAtrPips, 0.7, 1.5)
trailingAtr = Clamp(tfBase * volFactor, 1.5, 4.0)
```

---

## 5. Risk Profiles, Governor, FTMO

### 5.1 Three Risk Profiles

| Profile | Risk/Trade | Daily DD | Total DD | Max SL | Max Exposure | DD Scale | Max Positions |
|---------|-----------|----------|----------|--------|--------------|----------|---------------|
| Conservative | 0.25% | 3% | 6% | 50 pips | 3% | 50%@50% | 2 |
| Standard | 0.50% | 4% | 8% | 100 pips | 5% | 50%@50% | 3 |
| Aggressive | 2.00% | 5% | 10% | 150 pips | 10% | 25%@75% | 5 |

Each also has: `LotSizingMethod` (5 options), `FixedLots`, `FixedDollarRisk`, `KellyFraction`, `AntiMartingaleMultiplier`, `propFirmRuleSetId`, `sizeModifiers`.

### 5.2 Governor

| Feature | Default | Behavior |
|---------|---------|----------|
| Loss bands | 40%/60% DD | 40% DD → 0.5x; 60% DD → halt |
| Streak | 3/5 losses | 3 → half size; 5 → pause + 24-bar cooling |
| Profit lock | 60% of target | Locks profit, only risk-free trades |

Governor `Enabled` on the page + `ProtectionToggles.GovernorEnabled` are ANDed at `PreTradeGate`.

### 5.3 FTMO Rules

| Rule | Standard | Aggressive |
|------|----------|------------|
| Max Daily Loss | 5% (InitialBalance) | 8% |
| Max Total Loss | 10% (Fixed) | 15% |
| Profit Target | 10% | 20% |
| Min Trading Days | 4 | 4 |
| Daily Reset | 22:00 UTC (Europe/Prague) | Same |
| News Window | 30min before, 15min after (High impact) | Same |
| Weekend No-Open | After 20:00 UTC Friday | Same |

**ProtectionToggles** (9 booleans): gate each protection at the kernel level.

---

## 6. Kernel Flow — Detailed

### 6.1 PreTradeGate — 8-step Risk Walk

```
1. PROTECTION_MODE_ACTIVE → REJECT
2. Governor block (SoftStop/HardStop/CoolingOff/ProfitLocked) → REJECT
3. SL too wide (exceeds MaxSlPips) → REJECT
4. Max positions (global + per-strategy) → REJECT
5. Max exposure → REJECT
6. External verdicts (news/weekend/compliance) → REJECT
7. KernelSizing.Calculate → lot size (5 methods, drawdown-scaled)
8. Worst-case DD projection → REJECT or downsized
```

### 6.2 Lot Size Formula

```
riskAmount = equity × riskPerTradePercent × drawdownScale
pipValuePerLot = PipCalculator (3-branch: quote/base/cross currency logic)
rawLots = riskAmount / (slPips × pipValuePerLot)
scaledLots = rawLots × drawdownScale
clamped = min(scaledLots, maxLots)
stepped = floor(clamped / lotStep) × lotStep  ← Math.Floor, never Math.Round
finalLots = max(stepped, minLots)
```

### 6.3 Cost Computation

```
GrossPnL   = PipCalculator.GrossPnL(entry, exit, direction, lots, symbolInfo, crossRate)
Commission = lots × commissionPerLotPerSide × 2  (round turn)
Swap       = nightsHeld × swapRate(direction) × lots  (×3 on Wednesday)
NetPnL     = GrossPnL − Commission − Swap
```

### 6.4 KernelBacktestLoop — Per-bar Lifecycle

```
ProcessBarAsync(bar, state):
  1. AdvanceVenue(bar)          ← cross rates + broker.OnBarObserved
  2. PumpAsync()                ← drain prior venue feedback
  3. Emit day/week/month rolls  ← ResetClock detection
  4. BarEvaluator.EvaluateAsync ← strategy signals → OrderProposed
  5. PumpAsync()                ← kernel gate + sizing decisions
  6. EquityObserved             ← drawdown fold + breach watchdog
  7. TrailingEvaluator          ← trail/BE/partial TP → StopLossModifyRequested
  8. Venue.CompleteBarAsync()   ← bar complete
  9. ReportBar()                ← progress + equity snapshot + bar persistence
```

---

## 7. Journal System

### JournalEventKind (8 kinds)

| Kind | Meaning | When |
|------|---------|------|
| SIGNAL | Strategy emitted TradeIntent | BarEvaluator |
| ORDER | Order accepted by kernel | Kernel.DecideProposed |
| FILL | Order filled / position opened | KernelFeedback.FromExecution |
| CLOSE | Position closed (with exit reason + costs) | EffectExecutor.PublishTradeClosed |
| REJECTED | Order rejected by risk gate | PreTradeGate reject |
| BREACH | Drawdown limit breach | Kernel.DecideEquity |
| GOVERNOR | Governor state change | GovernorMachine |
| ENTRY_EXPIRED | Limit order expired unfilled | OrderCancelled |

Plus add-on journal kinds: `ADDON_RESOLVED`, `BREAKEVEN`, `TRAIL`, `PARTIAL`, `RIDE`

### ChannelJournalWriter
Wait-mode bounded channel. Every kernel step writes a `StepRecord`.
**API:** `GET /api/runs/{id}/journal?kind=&afterSeq=&limit=50` (SQL-paged)
**Export:** `GET /api/runs/{id}/journal/export` (NDJSON stream)

---

## 8. Code Map — Key Files

### Kernel core (pure):
| File | Purpose |
|------|---------|
| `src/TradingEngine.Engine/Kernel/Kernel.cs` | Decision dispatcher |
| `src/TradingEngine.Engine/Kernel/PreTradeGate.cs` | 8-step risk gate |
| `src/TradingEngine.Engine/Kernel/KernelSizing.cs` | Lot sizing (5 methods) |
| `src/TradingEngine.Engine/EngineReducer.cs` | 15 state transition handlers |
| `src/TradingEngine.Domain/RiskAndEquity/EngineState.cs` | Authoritative state |
| `src/TradingEngine.Engine/DrawdownReducer.cs` | Drawdown math |
| `src/TradingEngine.Engine/PositionLifecycle.cs` | Position FSM |
| `src/TradingEngine.Engine/GovernorMachine.cs` | Governor state machine |

### Host orchestration:
| File | Purpose |
|------|---------|
| `src/TradingEngine.Host/KernelBacktestLoop.cs` | Main engine loop |
| `src/TradingEngine.Host/BarEvaluator.cs` | Strategy → OrderProposed |
| `src/TradingEngine.Host/EffectExecutor.cs` | Kernel effect → venue |
| `src/TradingEngine.Host/EngineRunner.cs` | Composition root, initializes everything |
| `src/TradingEngine.Host/KernelFeedback.cs` | Venue → kernel event |
| `src/TradingEngine.Host/KernelTrailingEvaluator.cs` | Trailing/BE/partial add-on eval |
| `src/TradingEngine.Host/EngineHostFactory.cs` | DI wiring |
| `src/TradingEngine.Engine/Kernel/ChannelJournalWriter.cs` | Lossless journal |

### Strategies:
| File | Strategy |
|------|----------|
| `src/TradingEngine.Strategies/TrendBreakout/TrendBreakoutStrategy.cs` | Trend Breakout |
| `src/TradingEngine.Strategies/EmaAlignment/EmaAlignmentStrategy.cs` | EMA Alignment |
| `src/TradingEngine.Strategies/MeanReversion/MeanReversionStrategy.cs` | Mean Reversion |
| `src/TradingEngine.Strategies/SessionBreakout/SessionBreakoutStrategy.cs` | Session Breakout |
| `src/TradingEngine.Strategies/SuperTrend/SuperTrendStrategy.cs` | SuperTrend |
| `src/TradingEngine.Strategies/RsiDivergence/RsiDivergenceStrategy.cs` | RSI Divergence |
| `src/TradingEngine.Strategies/MtfTrend/MtfTrendStrategy.cs` | Multi-Timeframe Trend |
| `src/TradingEngine.Strategies/MacdMomentum/MacdMomentumStrategy.cs` | MACD Momentum |
| `src/TradingEngine.Strategies/BollingerSqueeze/BollingerSqueezeStrategy.cs` | Bollinger Squeeze |

### Services:
| File | Purpose |
|------|---------|
| `src/TradingEngine.Services/PipCalculator.cs` | Pip distance, pip value, gross PnL |
| `src/TradingEngine.Services/Helpers/TradeCostCalculator.cs` | Commission + swap |
| `src/TradingEngine.Services/Helpers/EntryPlanner.cs` | Limit order planning |
| `src/TradingEngine.Services/Helpers/EffectiveConfigResolver.cs` | Deep-merge config |
| `src/TradingEngine.Services/AddOns/AddOnResolver.cs` | Add-on resolution |
| `src/TradingEngine.Services/AddOns/AddOnAutoTuner.cs` | Auto-tune math |
| `src/TradingEngine.Services/PositionManager.cs` | Position management runtime |

### Venues:
| File | Purpose |
|------|---------|
| `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` | Replay venue (SQLite bars) |
| `src/TradingEngine.Infrastructure/Venues/Simulated/SimulatedBrokerAdapter.cs` | Synthetic venue (CSV ticks) |
| `src/TradingEngine.Infrastructure/Venues/CTrader/CTraderBrokerAdapter.cs` | cTrader venue (NetMQ) |

### Infrastructure:
| File | Purpose |
|------|---------|
| `src/TradingEngine.Infrastructure/Persistence/TradingDbContext.cs` | Primary EF Core context |
| `src/TradingEngine.Infrastructure/Persistence/StrategyConfigSeeder.cs` | JSON → DB seed |
| `src/TradingEngine.Infrastructure/Indicators/SkenderIndicatorService.cs` | Skender wrapper |
| `src/TradingEngine.Infrastructure/Indicators/IndicatorCache.cs` | Indicator key management |

### Web API:
| File | Purpose |
|------|---------|
| `src/TradingEngine.Web/Api/RunsController.cs` | Runs CRUD + journal + analytics |
| `src/TradingEngine.Web/Api/StrategiesController.cs` | Strategy CRUD |
| `src/TradingEngine.Web/Api/AddOnPacksController.cs` | Add-on pack CRUD |
| `src/TradingEngine.Web/Api/RiskProfilesController.cs` | Risk profile CRUD |
| `src/TradingEngine.Web/Api/PropFirmRulesController.cs` | FTMO rules CRUD |
| `src/TradingEngine.Web/Api/GovernorOptionsController.cs` | Governor CRUD |
| `src/TradingEngine.Web/Api/TradesController.cs` | Trades query |
| `src/TradingEngine.Web/Api/BarsController.cs` | Bars query |
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Backtest lifecycle |

### Angular UI:
| File | Purpose |
|------|---------|
| `web-ui/src/app/features/runs/run-list/` | Run list table + comparison |
| `web-ui/src/app/features/runs/new-backtest/` | New backtest form |
| `web-ui/src/app/features/runs/run-report/` | Post-run report |
| `web-ui/src/app/features/runs/run-monitor/` | Live monitor |
| `web-ui/src/app/features/runs/run-analyzer/` | Analytics histograms |
| `web-ui/src/app/features/strategies/` | Strategy list + detail editor |
| `web-ui/src/app/features/addon-packs/` | Add-on pack list + detail |
| `web-ui/src/app/shared/equity-chart.component.ts` | Equity + DD chart |
| `web-ui/src/app/shared/data-table.component.ts` | Reusable table component |
| `web-ui/src/app/shared/scatter-chart.component.ts` | MAE/MFE scatter |
| `web-ui/src/app/shared/histogram-chart.component.ts` | Distribution histogram |
| `web-ui/src/app/core/signalr/run-hub.service.ts` | SignalR connection |
| `web-ui/src/app/models/api.types.ts` | All TypeScript interfaces |

### Config files:
| File | Purpose |
|------|---------|
| `config/strategies/*.json` | 9 strategy configs (seed → DB) |
| `config/risk-profiles/{conservative,standard,aggressive}.json` | Risk profiles |
| `config/prop-firms/{ftmo-standard,ftmo-aggressive}.json` | FTMO rules |
| `config/symbols.json` | 17 instrument definitions |
| `config/governor.json` | Governor options |
| `config/sizing-policy.json` | Sizing policy |
| `config/regime.json` | Regime detection settings |
| `config/rotation.json` | Strategy rotation (disabled) |

---

## 9. Test Architecture

| Tier | What Proves | How | Count |
|------|-----------|-----|-------|
| Architecture | Layer boundary enforcement | Reflection | 5 |
| Unit | Individual component correctness | xUnit + NSubstitute, no DI/DB | ~267 |
| Integration | DI resolution, DB, HTTP endpoints | Real DI + SQLite | ~67 |
| Simulation | Full engine end-to-end | Harness + real venue | ~82 (cred-free) |

### Key simulation tests:
- `FtmoGoldenJourneyTests` — FTMO rule enforcement over multi-bar scenarios
- `BacktestReplayCostsAndLimitsTests` — Cost computation + limit orders
- `KernelDeterminismTests` — Byte-identical replay
- `GovernorDrawdownProtectionTests` (12) — Governor + DD behavior
- `FtmoPressureTests` (4) — Daily loss halts, max-loss terminal
- `JournalSourceOfTruthTests` (9) — One StepRecord per event, ORDER↔FILL join
- `AddOnAutoTunerTests` — Deterministic auto-tuner
- `AddOnPackConfigFlowTests` — Pack application correctness

### cTrader E2E (credential-gated):
- `CtraderPipelineDiagnosticTest` — 3/30 day EURUSD H1
- `CtraderScenarioTests` — Trade ledger integrity, multi-symbol
- `FullBacktestPipelineTest` — 3-month EURUSD H1, multi-symbol parametric
- 16 cTrader tests total, require `CTrader:CtId`, `CTrader:PwdFile`, `CTrader:Account`

---

## 10. Field Type Mapping Across Layers

| Domain (C#) | DB Entity | API DTO | Angular TS |
|---|-----------|---------|---------|
| `Guid` | `Guid` | `Guid` | `string` |
| `decimal` | `decimal` | `decimal` | `number` |
| `double` | `double` | `double` | `number` |
| `int` | `int` | `int` | `number` |
| `long` | `long` | `long` | `number` |
| `bool` | `bool` | `bool` | `boolean` |
| `DateTime` | `DateTime` | `DateTime` | `string` (ISO 8601) |
| enum | string | string | `string` |
| `Dictionary<K,V>` | JSON string | JSON string | `Record<K,V>` |
| `List<T>` | JSON string | `List<T>` | `T[]` |
