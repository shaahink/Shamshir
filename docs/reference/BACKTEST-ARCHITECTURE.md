# Backtest Architecture

**Last updated**: 2026-06-18 (post iter-31/32)

This document explains how backtesting actually works — both venue paths, data flow,
cost computation, limit orders, and journal capture. Read this before touching any
backtest-related code.

---

## The two venue paths

### Path A — BacktestReplayAdapter (default, credential-free)

```
BacktestOrchestrator.Start()
  → RunEngineReplayAsync() (when CTrader:UseForBacktest=false)
    → spins up engine in-process via IHostBuilder
      → registers BacktestReplayAdapter as IBrokerAdapter
      → engine uses EngineMode.Backtest
    ↓
BacktestReplayAdapter.ConnectAsync()
  → loads bars from SQLite Bars table for (symbol, timeframe, from, to)
  → feeds bars + synthetic ticks to engine
  → for each bar:
      → evaluates strategies via TradingLoop
      → EntryPlanner resolves limit/market orders
      → fills market orders at bar close, rests limit orders
      → cancels expired limit orders (OrderCancelled event)
      → computes commission + swap via TradeCostCalculator
      → stamps ExecutionEvent with GrossProfit/Commission/Swap/NetProfit
      → emits AccountUpdate with net-updated balance
      → drains execution events
  ↓
Journal captured: PipelineEvents (SIGNAL/ORDER/FILL/CLOSE/ENTRY_EXPIRED)
  with itemized cost detail on every close
```

**Status**: Working. Default path when `CTrader:UseForBacktest=false` (Development profile currently has this at `true` — set to `false` for credential-free). Produces honest cost-inclusive results. Supports limit orders with resting/expiry semantics.

### Path B — SimulatedBrokerAdapter (synthetic, tick-driven)

```
DataFeedService (IHostedService)
  → reads CSV from HistoricalDataProvider (committed test data in tests/data/)
  → synthesizes 4 ticks per bar (0%/25%/50%/75% duration)
  → writes to SimulatedBrokerAdapter's TickWriter / BarWriter
  ↓
SimulatedBrokerAdapter.OnTickReceived()
  → fills market orders on next tick
  → rests limit orders until price reached or expiry
  → computes costs via TradeCostCalculator
  → checks SL/TP hits per tick
  ↓
Used primarily by EngineTestHarness in simulation tests.
Also registered for EngineMode.Backtest in Host Program.cs, but the default
UI path (RunEngineReplayAsync) uses BacktestReplayAdapter.
```

### Path C — cTrader (production-equivalent, requires credentials)

```
BacktestOrchestrator.Start() (CTrader:UseForBacktest=true)
  → BacktestRunner.RunAsync()
    → launches ctrader-cli as external Process
      → cBot connects to engine via NetMQ
      → engine uses NetMQBrokerAdapter (lock-step protocol)
      → cTrader handles fill simulation, costs, SL/TP
```

**Status**: Requires CTrader credentials (`CTrader:CtId`, `CTrader:PwdFile`, `CTrader:Account`). Cannot be automated in CI. NetMQ ports 15555/15556 must be free.

---

## Cost computation (iter-31)

Both credential-free venues use a shared `TradeCostCalculator` (in `TradingEngine.Services/Helpers/`):

```
closeExecutionEvent.GrossProfit = PipCalculator.GrossPnL(entry, exit, direction, lots, symbolInfo, crossRate)
closeExecutionEvent.Commission   = lots × commissionPerLotPerSide × 2  (round turn)
closeExecutionEvent.Swap         = nightsHeld × swapRate(direction) × lots
closeExecutionEvent.NetProfit    = GrossProfit − Commission − Swap
```

- `nightsHeld` counts daily-rollover boundaries crossed between open/close
- Triple-swap day (default Wednesday) counts ×3
- Cost data lives in `SymbolInfo` (+ `config/symbols.json`): `CommissionPerLotPerSide`, `SwapLongPerLotPerNight`, `SwapShortPerLotPerNight`, `TripleSwapWeekday`
- All fields default to 0 (costs off, back-compatible)

---

## Limit order support (iter-31 C0/C1)

`EntryPlanner` (single place, in TradingLoop after `strategy.Evaluate()`) reads `OrderEntryOptions` from config:

| Method | Behavior |
|--------|----------|
| `Market` | Immediate fill at bar close + slippage |
| `LimitOffset` | Place limit `offsetPips` more favorable; rest until price reaches limit; expire after `limitOrderExpiryBars` |
| `MarketWithSlippage` | Immediate fill capped at `maxSlippagePips` |

- Buy limit fills when `Ask ≤ limitPrice`, at limit price (no slippage)
- Sell limit fills when `Bid ≥ limitPrice`, at limit price
- Expired limits emit `OrderCancelled` → journal as `ENTRY_EXPIRED`
- SL/TP re-derived off the planned limit so R stays consistent
- Default: all strategies `Market`; `mean-reversion` on `LimitOffset` as demonstration

---

## Journal taxonomy

All pipeline events are normalized via `JournalNormalizer` into one taxonomy:

| Kind | Meaning |
|------|---------|
| `SIGNAL` | Strategy emitted a TradeIntent (reason + direction + indicators) |
| `ORDER` | Order accepted by dispatcher (size, risk, profile) |
| `FILL` | Order filled / position opened |
| `CLOSE` | Position closed (SL/TP/FORCE/DailyDD/MaxDD) with itemized costs |
| `REJECTED` | Order rejected by risk gate |
| `BREACH` | Drawdown limit breach detected |
| `GOVERNOR` | Governor (session) state change |
| `ENTRY_EXPIRED` | Limit order expired unfilled |

API: `GET /api/backtest/{runId}/journal?kind=&afterSeq=&limit=50`

---

## Data flow: where RunId comes from and how it reaches the DB

```
BacktestOrchestrator.Start()
  → generates RunId = Guid.NewGuid().ToString("N")[..8]
  → BacktestOrchestrator.RunEngineReplayAsync()
    ↓
creates inner IHost with EngineHostOptions(runId)
    ↓
EngineWorker gets EngineRunContext injected
  → all handlers stamp RunId on every record
    ↓
TradePersistenceHandler  → Trades table (RunId)
BarEvaluationHandler     → BarEvaluations table (RunId)
EquityPersistenceHandler → EquitySnapshots table (RunId)
PipelineEventWriter      → PipelineEvents table (RunId)
BacktestRunEntity        → BacktestRuns table (RunId, EffectiveConfigJson)
```

---

## Key channel modes

| Channel | Location | Mode | Capacity | Why |
|---------|----------|------|----------|-----|
| BarStream | IBrokerAdapter | DropOldest | 2,000 | Market data; replay uses separate pacing |
| TickStream | IBrokerAdapter | DropOldest | 10,000 | Ticks for fills only; can drop |
| ExecutionStream | IBrokerAdapter | Wait | 1,000 | Never drop fills |
| _executionEventChannel | EngineWorker | Wait | 1,000 | Internal relay for execution events |
| TradePersistenceHandler._channel | Handler | Wait | 1,000 | Never drop trade events |
| BarEvaluationHandler._channel | Handler | DropOldest | 50,000 | Analytics; dropping old is OK |

---

## Where bars come from in each mode

| Mode | Bar source | Seeded by |
|------|-----------|-----------|
| BacktestReplayAdapter | `IBarRepository.GetAsync()` → SQLite `Bars` table | Prior cTrader backtest or fixture data |
| SimulatedBrokerAdapter | `HistoricalDataProvider` → CSV files (`tests/data/`) | `CsvDataGenerator` (seeded deterministic) |
| cTrader backtest | cBot via NetMQ | ctrader-cli replay |
| Live | cBot via NetMQ | Live market |

---

## Schema management

Schema is maintained via EF Core migrations only. The raw SQL `ALTER TABLE` patches
in startup files were removed in Iteration 18. Current migration: single `InitialCreate`
capturing the full schema.

**Rule**: All schema changes via EF migrations. Run:
```
dotnet ef migrations add <Name> --startup-project src/TradingEngine.Web --project src/TradingEngine.Infrastructure
```

---

## Current test status

| Suite | Count | Requires credentials |
|-------|-------|---------------------|
| Unit | ~207 pass, 4 skipped | No |
| Simulation (FtmoGolden) | 4 pass | No |
| Simulation (Replay) | Several E2E | No |
| Architecture | 3 pass | No |
| Integration | 35 pass | No |
| cTrader E2E | Requires credentials | Yes |

Run: `dotnet test tests/TradingEngine.Tests.Unit` + `tests/TradingEngine.Tests.Simulation`
