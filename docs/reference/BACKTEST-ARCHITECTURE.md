# Backtest Architecture

**Last updated**: Iteration 10

This document explains how backtesting actually works in this system — both paths, their
status, and where the data flows. Implementing agents must read this before touching any
backtest-related code.

---

## The two backtest paths

### Path A — cTrader (production-equivalent)

```
User clicks "Run Backtest" in Web UI
    ↓
BacktestOrchestrator.Start(cfg)
    ↓
BacktestRunner.RunAsync(cfg)        [TradingEngine.CTraderRunner]
    ↓ (optionally)
StartEngine() → dotnet run TradingEngine.Host (subprocess)
    → reads Engine__RunId from env
    → registers EngineRunContext(runId)
    → registers SimulatedBrokerAdapter (NOT BacktestReplayAdapter)
    → EngineWorker starts: ProcessBarsAsync, ProcessTicksAsync etc.
    ↓
Process.Start("ctrader-cli backtest ...")
    → cBot starts inside ctrader-cli sandbox
    → cBot connects to engine via NetMQ (ZeroMQ, tcp://127.0.0.1:15555)
    → cBot sends bars via NetMQ PUB → engine receives on BarStream
    → engine evaluates strategies → signals → OrderDispatcher
    → OrderDispatcher → broker.SubmitOrderAsync → cBot fills via NetMQ DEALER
    → ExecutionEvent → PositionTracker → TradeClosed event
    → TradePersistenceHandler → SQLite Trades table (with RunId)
    → BarEvaluationHandler → SQLite BarEvaluations table (with RunId)
    ↓
ctrader-cli exits → writes report.json
    ↓
BacktestRunner reads report.json → BacktestResult
    ↓
BacktestOrchestrator queries DB for trade stats (overrides report.json stats)
    ↓
BacktestRuns row saved to DB
```

**Status**: Working, but:
- Requires CTrader credentials (`CTrader:CtId`, `CTrader:PwdFile`, `CTrader:Account`)
- Cannot be automated in CI
- Max drawdown stat is wrong (see BUG-04 in `docs/OPEN-ISSUES.md`)
- BacktestRuns saved only on success (see DESIGN-05)

---

### Path B — Engine Replay (development, CI)

```
BacktestOrchestrator (or test harness)
    ↓
Spins up engine in-process (IHostBuilder)
    → registers BacktestReplayAdapter as IBrokerAdapter
    → registers EngineRunContext(runId)
    → EngineWorker starts: ProcessBarsAsync, ProcessTicksAsync etc.
    ↓
BacktestReplayAdapter.ConnectAsync()
    → loads bars from SQLite Bars table for (symbol, timeframe, from, to)
    → for each bar:
        → writes bar to BarStream channel
        → writes synthetic tick at bar.Close to TickStream
        → [PLANNED] fills any pending orders at bar.Close
        → [PLANNED] emits AccountUpdate with updated equity
    ↓
EngineWorker processes bars → strategy evaluates → signals → OrderDispatcher
    → OrderDispatcher → adapter.SubmitOrderAsync → pending order
    → [PLANNED] adapter fills it → ExecutionEvent → PositionTracker
    → TradeClosed → TradePersistenceHandler → DB
```

**Status as of Iteration 10**: BROKEN
- BUG-01: `SimulateFill` exists but is never called → 0 trades always
- BUG-02: Bar channel capacity 2,000 with `DropOldest` — ConnectAsync writes all bars before
  consumer starts → silent data loss for ranges > 2,000 bars
- BUG-03: `ClosePositionAsync` sends null fill price → force-close silently discarded
- NOT wired to the UI — the "Run Backtest" button always uses Path A

**Target state after Iteration 11**: BUG-01, 02, 03 fixed. E2E test passes without credentials.
**Target state after Iteration 12**: BacktestOrchestrator uses Path B by default; Path A available
as explicit "cTrader mode".

---

## Data flow: where RunId comes from and how it reaches the DB

```
BacktestOrchestrator.Start()
    → generates RunId = Guid.NewGuid().ToString("N")[..8]
    → stamps BacktestConfig.RunId = runId
    ↓
BacktestRunner passes RunId as env var: Engine__RunId = runId
    ↓
TradingEngine.Host Program.cs reads Engine:RunId from config
    → registers singleton: EngineRunContext(runId)
    ↓
EngineWorker gets EngineRunContext injected
    ↓
PositionTracker.ClosePosition:
    → fires TradeClosed(tradeResult, runContext.RunId, clock.UtcNow)
    ↓
TradePersistenceHandler handles TradeClosed
    → PersistenceService.SaveTradeAsync(trade, runId, ct)
    → SqliteTradeRepository sets TradeResultEntity.RunId
    ↓
BarEvaluationHandler handles BarEvaluated
    → saves BarEvaluationEntity.RunId
```

---

## Key channel modes

| Channel | Location | Mode | Capacity | Why |
|---------|----------|------|----------|-----|
| BarStream | IBrokerAdapter | DropOldest | 2,000 | Market data; replay uses higher capacity |
| TickStream | IBrokerAdapter | DropOldest | 10,000 | Ticks for fills only; can drop |
| ExecutionStream | IBrokerAdapter | Wait | 1,000 | Never drop fills |
| _executionEventChannel | EngineWorker | Wait | 1,000 | Internal relay for execution events |
| TradePersistenceHandler._channel | Handler | Wait | 1,000 | Never drop trade events |
| BarEvaluationHandler._channel | Handler | DropOldest | 50,000 | Analytics; dropping old is OK |

---

## Where bars come from in each mode

| Mode | Bar source | Seeded by |
|------|-----------|-----------|
| cTrader backtest | cBot via NetMQ | ctrader-cli replay |
| Engine replay | `IBarRepository.GetAsync()` → SQLite `Bars` table | Prior cTrader backtest |
| Live | cBot via NetMQ | Live market |

**Important**: For the engine replay path to work, the `Bars` table must already contain data
for the requested symbol/timeframe/date range. This data is populated when a cTrader backtest
runs and the cBot sends bars to the engine (they're saved by `IBarRepository`).

---

## Ports used (NetMQ)

| Port | Use | Configurable |
|------|-----|-------------|
| 15555 | PUB/SUB data stream (bars, ticks, account) | `Engine:Broker:NetMQ:DataPort` |
| 15556 | ROUTER/DEALER command channel (orders, fills) | `Engine:Broker:NetMQ:CommandPort` |

Both ports must be free when a cTrader backtest runs. Parallel runs would conflict (D74).

---

## Schema management (current state — tech debt)

Schema is maintained in two places simultaneously:

1. **EF Core** (`TradingDbContext.OnModelCreating`) — the authoritative definition
2. **Raw SQL in startup** (`Web/Program.cs:28–37`, `Host/Program.cs:118–119`) — `ALTER TABLE`
   and `CREATE TABLE IF NOT EXISTS` patches applied at startup

This is tech debt from using `EnsureCreated()` without migration history. The raw SQL patches
cannot be removed until a full migration baseline is established (Iteration 15 target).

**Rule**: Do not add more raw SQL patches. If adding a column, add an EF migration AND update
the EF entity. Do not rely on the startup patch pattern for new schema.
