# Shamshir — Open Issues, Technical Debt, and Next Direction

**Written**: 2026-06-08 (post-Iteration-10 code review)
**Branch at time of writing**: `phase/8b-bar-tracing`
**Last updated**: 2026-06-11 (post-Iteration-17)

This document is the authoritative issues log. Update it as items are fixed or new findings are added.
Never delete a resolved item — mark it `✅ Fixed (Iteration N)` so there is a record.

---

## Iteration 17 Resolved Items

The following items were addressed in Iteration 17 (`iter/17-deterministic-pipeline`):

- **NetMQ thread-affinity bug** (orders silently lost) → Fixed with `NetMQQueue<T>` (Phase A1)
- **Sleep-based synchronization** (`Thread.Sleep(600)`, 10×500ms heartbeats) → Replaced with `hello`/`hello_ack` handshake (Phase A2)
- **Shutdown data loss** (no linger, final execs dropped) → Fixed with `Linger=2s`, `stats` message, drain on stop (Phase A3)
- **DI block copied in 3 places** (divergent) → `EngineHostFactory` single composition root (Phase B1)
- **EngineMode type-sniffing** (`_broker is SimulatedBrokerAdapter`) → Explicit `EngineMode` parameter (Phase B1)
- **Hardcoded SymbolInfo** (`new SymbolInfo(symbol, ..., "EUR", "USD", ...)`) → `config/symbols.json` + `SymbolCatalog` (Phase B2)
- **CrossRateStore double-instance bug** (two separate instances) → Single instance registered (Phase B1)
- **Dual execution-event consumer** (ProcessExecutionEventsAsync vs DrainExecutionStreamAsync) → Single consumer via double-drain (Phase B3)
- **Lock-step protocol** (barrier per bar, deterministic execution) → Implemented in cBot + engine (Phase C)
- **Symbol wrong for GBPUSD/AUDUSD backtests** (defaulted to EURUSD) → Fixed in controller + page model (Phases B2, post-C fix)
- **EF Core SQL log flood** (2000+ INSERT dumps) → `.AddFilter("Microsoft.EntityFrameworkCore", Warning)` (post-C fix)
- **Close position ID mismatch** (Guid ≠ long) → Fixed with `_positionMap` reverse-lookup (post-C fix)
- **PipelineEvents journal** (per-run event log) → Entity, mapping, writer, repository (Phase D1)
- **Unified logging** (4 methods → 1 `BacktestJournal`) → Consolidation (Phase D2)
- **Multi-symbol/timeframe UI** (checkboxes for 12 symbols, 6 timeframes) → (Phase 1)

---

## Part 1 — Critical Bugs (must fix before trusting any backtest results)

### BUG-01 — BacktestReplayAdapter never fills orders
**Severity**: Critical — makes every replay produce 0 trades  
**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs`

✅ **Fixed (Iteration 11)**. `SubmitOrderAsync` now writes `ExecutionEvent` directly to
`_executionChannel` at bar close price. The `_pendingOrders` dictionary and unused
`SimulateFill` method have been removed.

---

### BUG-02 — BacktestReplayAdapter silently drops bars for ranges > 2,000 bars
**Severity**: Critical — corrupts backtest results  
**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs`

✅ **Fixed (Iteration 11)**. `_barChannel` and `_tickChannel` changed to unbounded channels.
`ConnectAsync` now starts `FeedBarsAsync` as a background task and returns immediately,
allowing the engine's consumer loops to start before all bars are written.

---

### BUG-03 — Force-close silently does nothing; exit reason always wrong
**Severity**: Critical  
**File**: `src/TradingEngine.Services/PositionTracker.cs` and
`src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs`

✅ **Fixed (Iteration 11)**. `ClosePositionAsync` now sends current `_lastClose` as fill price
(instead of `null`). `PositionTracker.ClosePosition` replaced incorrect ternary with
`DetermineExitReason` method: checks TP existence before returning "TP", returns "FORCE"
when neither SL nor TP applies.

---

### BUG-04 — Max drawdown calculation is fabricated

✅ **Fixed (Iteration 12)**. `GetTradeStatsAsync` now builds a cumulative equity curve from trades ordered by `ClosedAtUtc` and computes peak-to-trough drawdown: `dd = (peak - equity) / peak`.
**Severity**: Critical — the stat shown in the UI is meaningless  
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:64`

```csharp
var maxDd = Math.Abs(trades.Min(t => t.NetPnLAmount)) / 100_000m;
```

This computes `worst_single_trade_PnL / 100,000`. It is not drawdown. It ignores:
- The initial balance (hardcoded to 100,000 regardless of config)
- The equity curve (peak-to-trough, not single worst trade)
- Multiple consecutive losses

**Fix**: Build a running equity curve from trade sequence, find peak, find subsequent trough,
express as percentage of peak. The `BacktestReplayAdapter` should emit `AccountUpdate` events
after each simulated close so `DrawdownTracker` accumulates the real curve.

---

### BUG-05 — Hardcoded cross-rates cause wrong lot sizing for JPY/GBP pairs in live mode
**Severity**: Critical for live; incorrect in backtest  
**File**: `src/TradingEngine.Host/Program.cs:101–106`

✅ **Fixed (Iteration 16)**. Created `CrossRateStore` class with mutable `GbpUsdRate` and `UsdJpyRate` fields. Registered as singleton in all DI paths (Web orchestrator, Host engine, test harness). `RunBacktestLoopAsync` updates cross-rates per bar based on the primary symbol's close price (GBPUSD bar → GBP→USD rate, USDJPY bar → JPY→USD rate). CrossRateStore is injected into EngineWorker constructor.

```csharp
if (from == "JPY" && to == "USD") return 1m / 149.50m;
if (from == "GBP" && to == "USD") return 1.2650m;
```

Domain rule: cross-rate pip values must be recalculated per tick (they change with price).
These constants are stale. GBPUSD fluctuates ±5% over months. Using a wrong cross-rate means
lot sizes are off proportionally — a 5% rate error = 5% too many or too few lots.

**Fix**: For backtest, read rates from the bar close prices of the cross pairs. For live, pull
from the broker's `AccountUpdate`. The cross-rate provider `Func<string, string, decimal>` is the
right abstraction — replace the singleton closure with one that reads live prices.

---

### BUG-06 — BacktestReplayAdapter reports flat equity forever; loss limits unenforceable
**Severity**: Critical — explains backtests that violated daily-loss limits
**Found**: 2026-06-12 (iter-19 planning audit)
**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs:92`

`FeedBarsAsync` emits `new AccountUpdate(_initialBalance, _initialBalance, 0, …)` on every bar.
Equity and balance are hardcoded to the initial balance for the whole run: realized PnL never
reaches the balance, floating PnL never reaches equity. `DrawdownTracker` therefore computes 0%
drawdown always, and `DAILY_DD_LIMIT`/`MAX_DD_LIMIT` can mathematically never fire in backtests.

**Fix**: planned as iter-19 Phase G0.1 (mark-to-market: balance updated on closes, equity =
balance + floating PnL at bar close).

---

### BUG-07 — `EnterProtectionMode` has no callers; daily breach never flattens positions
**Severity**: Critical
**Found**: 2026-06-12 (iter-19 planning audit)
**File**: `src/TradingEngine.Risk/RiskManager.cs:49`

Protection mode is dead code — nothing in the engine calls `EnterProtectionMode`. Additionally,
`_forceClosePending` is only set for `ProtectionCause.MaxDrawdown` with `ForceCloseOnBreach`, so
even if wired, a *daily* limit breach would block new entries but leave open positions running
unbounded. Risk checks gate new entries only, at `>=` the hard limit.

**Fix**: planned as iter-19 Phase G0.2 (breach watchdog in `HandleAccountUpdate`, force-close on
both daily and max-DD causes, flatten at 0.9 × hard limit).

---

### BUG-08 — MAX_EXPOSURE pre-check is a no-op for market orders
**Severity**: High
**Found**: 2026-06-12 (iter-19 planning audit)
**File**: `src/TradingEngine.Risk/RiskManager.cs:96`

`Validate` derives the entry price as `intent.LimitPrice ?? intent.StopLoss` — for market orders
(no limit price) the SL distance is measured from the SL itself, i.e. 0 pips, so the computed new
position risk is 0 and the exposure check always passes. It also assumes `1.0m` lots rather than
the lots that will actually be submitted.

**Fix**: planned as iter-19 Phase G0.3 (use current mid as entry price, compute risk on sized lots).

---

## Part 2 — Serious Design Problems

### DESIGN-01 — `TradeClosed` published fire-and-forget from `PositionTracker`
**File**: `src/TradingEngine.Services/PositionTracker.cs:117`

```csharp
_ = eventBus.PublishAsync(new TradeClosed(...), CancellationToken.None);
```

Trade persistence runs via this event. If the process shuts down before the task is scheduled, the
trade is lost. `TradeClosed` is the critical financial event and must not be fire-and-forget.

**Fix**: Return the task from `ClosePosition`, bubble it up through `OnExecution`, and await it
in `ProcessTicksAsync`. Or use `ValueTask` and fire synchronously through a synchronous event bus
path for in-process handlers.

---

### DESIGN-02 — Execution events only drained when ticks arrive
**File**: `src/TradingEngine.Host/EngineWorker.cs:144`

✅ **Fixed (Iteration 16)**. Added `await DrainExecutionStreamAsync()` call in `ProcessBarsAsync` (Live mode path) after the strategy evaluation foreach loop. This drains pending execution events on every bar in both Live and Backtest modes.

Fills from the broker are forwarded to `_executionEventChannel` by `ProcessExecutionEventsAsync`,
but that channel is only drained inside `ProcessTicksAsync` via `TryRead`. If no tick arrives
after a fill (possible in bar-replay mode where ticks are synthetic), the fill sits unprocessed.

**Fix**: Drain execution events in a dedicated processing step, not piggy-backed on the tick loop.
In the bar loop, after writing to the bar channel, explicitly drain pending executions before
evaluating strategies.

---

### DESIGN-03 — `Cancel()` doesn't kill the running process
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:102`

✅ **Fixed (Iteration 16)**. `BacktestRunState` stores `CancellationTokenSource` per run (implemented in iter-15). `Cancel()` calls `state.CancellationSource?.Cancel()`. The engine now runs in-process (Phase B) — no orphan subprocess. The ctrader-cli subprocess receives the cancellation token via CliWrap, which kills the process on cancellation. The per-run `CancellationTokenSource` links to a 30-minute timeout plus the orchestrator's cancellation chain.

`Cancel()` sets an in-memory status flag. The `ctrader-cli` subprocess and the engine subprocess
continue running. No `CancellationTokenSource` is stored per run; no process.Kill() is called.

**Fix**: Store `(Process?, CancellationTokenSource)` per run in `BacktestRunState`. `Cancel()` calls
`cts.Cancel()` and `process?.Kill(entireProcessTree: true)`.

---

### DESIGN-04 — `_processedExecutionIds` HashSet is an unbounded memory leak
**File**: `src/TradingEngine.Services/PositionTracker.cs:19`

Every `Guid` ever seen is added to `HashSet<Guid>` and never removed. A 3-month M1 backtest
could generate tens of thousands of fills. Over a live session running months, this grows forever.

**Fix**: Remove the ID from the set when the position is closed (or after the order has been fully
processed). The set's purpose is deduplication during the open→fill window, not permanent history.

---

### DESIGN-05 — Failed backtests create orphaned trade records

✅ **Fixed (Iteration 12)**. `WriteStartRecordAsync` writes an in-progress record (ExitCode=-1) at run start. `WriteEndRecordAsync` updates with final stats on completion or failure. Succeeded runs use `UpdateAsync`; failed runs still get a record.
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:147`

DB write to `BacktestRuns` only happens on `result.Success`. But `TradePersistenceHandler` saves
trades independently as they close — those trades carry the RunId but no corresponding `BacktestRuns`
row ever exists. Any join on RunId → BacktestRuns silently returns nothing for failed runs.

**Fix**: Always write the `BacktestRuns` row (with status/error). Use a two-step: insert a "started"
row at the beginning of `RunAsync`, update it with final stats on completion (success or fail).

---

### DESIGN-06 — `BarEvaluationHandler` drops remaining events silently on shutdown

✅ **Fixed (Iteration 13)**. `DisposeAsync` now drains remaining channel events via `TryRead` and
flushes them to the DB synchronously after the main flush task completes.
**File**: `src/TradingEngine.Host/BarEvaluationHandler.cs:71`

`DisposeAsync` cancels `_cts`, which causes the `Task.Delay(3_000, ct)` to throw
`OperationCanceledException` → `break`. Any events remaining in the 50,000-capacity channel are
silently dropped. After a backtest, thousands of bar evaluations may never be persisted.

**Fix**: After breaking out of the loop, do one final drain pass of whatever remains in the channel
before returning from `DisposeAsync`.

---

### DESIGN-07 — `BacktestOrchestrator.RunAsync` is fire-and-forget with no shutdown drain
**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs:85`

✅ **Fixed (Iteration 16)**. `BacktestRunState` now has a `RunTask` property. `Start()` stores the task via `state.RunTask = RunAsync(...)` instead of `_ = RunAsync(...)`. Added `StopAllAsync()` method that cancels all CTS tokens and awaits all in-flight tasks for graceful shutdown.

```csharp
_ = RunAsync(runId, cfg);
```

App shutdown doesn't await in-flight backtests. Multiple simultaneous backtests have no backpressure.

**Fix**: Store the `Task` in `BacktestRunState`. Implement `IHostedService` or `IAsyncDisposable` on
`BacktestOrchestrator` to await all tracked tasks on shutdown.

---

## Part 3 — Code Standard Violations

### STD-01 — `await Task.CompletedTask` cargo-cult in multiple files
- `BarEvaluationHandler.HandleAsync` — synchronous `TryWrite`, then `await Task.CompletedTask`
- `BacktestReplayAdapter.DisposeAsync` — `await Task.CompletedTask` with no async work
- `RecomputeIndicatorsAsync` — purely synchronous CPU work in `async Task` with no yield
- `WarmUpIndicatorsAsync` — just logs, no async work

Methods that don't await anything should not be `async`. Either remove `async` and return
`Task.CompletedTask` directly, or add `ValueTask` where the interface requires it.

---

### STD-02 — `MeanReversionStrategy` uses `double` for price arithmetic
**File**: `src/TradingEngine.Strategies/MeanReversion/MeanReversionStrategy.cs:55–56`

```csharp
var nearLow = (double)(latestBar.Close - latestBar.Low) / (double)latestBar.Close < 0.002;
```

`Close` and `Low` are `decimal`. Explicit cast to `double` for the division violates the domain rule
"always use decimal for price/money arithmetic". Use `decimal` throughout; the comparison `< 0.002m`
works fine.

---

### STD-03 — `BAR_EVAL` logged at `Information` level on every bar

✅ **Fixed (Iteration 13)**. Changed to `LogDebug`.
**File**: `src/TradingEngine.Host/EngineWorker.cs:199`

A 6-month H1 backtest = 4,000+ `Information` log lines from this alone. Should be `Debug` or `Trace`.

---

### STD-04 — Bare `catch { }` in `ResolveHalfSpread` silently swallows failures
**File**: `src/TradingEngine.Host/EngineWorker.cs:379`

Unknown symbol → spread fallback to `0.00005m` with no log. Should `LogWarning` so symbol config
gaps are visible.

---

### STD-05 — `IEnumerable<IStrategy>` enumerated multiple times
**File**: `src/TradingEngine.Host/EngineWorker.cs`

`_strategies` is `IEnumerable<IStrategy>`. Called with `.Count()` at startup, iterated per bar in
`ProcessBarsAsync`, and iterated again in `WarmUpIndicatorsAsync`. If DI registers the factory as
non-singleton, strategies are recreated on every iteration. Should be materialized to
`IReadOnlyList<IStrategy>` in the constructor.

---

### STD-06 — `CancellationToken` missing on async methods
`RecomputeIndicatorsAsync` and `WarmUpIndicatorsAsync` are `async Task` but accept no
`CancellationToken`. Code standard: CT required on every async method.

---

### STD-07 — `BarEvaluations` schema in raw SQL in `Web/Program.cs`, not in EF migration
**File**: `src/TradingEngine.Web/Program.cs:34–36`

✅ **Fixed (Iteration 18)**. Raw SQL removed, replaced with proper EF migration (`InitialFullSchema` in
Persistence/Migrations). Web startup uses `MigrateAsync()` instead of `EnsureCreated()` + ALTER TABLE.

---

## Part 4 — Observability Gaps (what you can't currently see)

### OBS-01 — No visibility into bar flow during backtest
When running a backtest from the UI, there is no way to observe:
- How many bars were loaded from the DB
- How many bars were written to the channel vs dropped (BUG-02)
- How many bars were consumed by the engine
- Whether the bar processor is keeping up or falling behind

**What's needed**: Metrics or log lines (at Debug) showing:
```
REPLAY_LOADED|Symbol=EURUSD|Tf=H1|Bars=4320
BAR_WRITTEN|n=1|OpenTime=2024-01-02 00:00|Close=1.09320
BAR_CONSUMED|n=1|Strategy=mean-reversion|IndicatorCount=3
```

---

### OBS-02 — No visibility into signal evaluation during backtest
Currently `BarEvaluated` events are written to the DB every 3 seconds by `BarEvaluationHandler`,
but this is not surfaced in the UI during the run. You cannot tell from the UI:
- How many bars evaluated
- How many had insufficient bars (warmup phase)
- How many had RSI/indicator conditions not met
- How many fired a signal but were rejected by risk
- How many signals resulted in a submitted order

**What's needed**: A live event feed on the Progress page showing these categories in real-time
as the backtest runs.

---

### OBS-03 — No visibility into order lifecycle
Between `SIGNAL` and `TRADE_SAVED`, there are several failure points:
- Order submitted to broker (`SubmitOrderAsync`)
- Execution event received (fill or reject)
- Position opened
- Position closed (SL/TP/force)
- Trade persisted

None of these are surfaced in the UI. You only see the final trade count at the end. If 10 signals
fired and 8 were rejected by risk, you see "2 trades" with no explanation.

**What's needed**: `OrderLifecycleEvent` log entries or structured log lines that can be queried
per RunId: `SIGNAL_FIRED → ORDER_SUBMITTED → ORDER_FILLED → POSITION_OPENED → POSITION_CLOSED_TP`.

---

### OBS-04 — No equity curve data captured during backtest
The `BacktestReplayAdapter` sends a single `AccountUpdate` at the start (initial balance) and never
again. The equity curve is flat. The backtest detail page can't show drawdown over time because
there's no per-trade equity update.

✅ **Fixed (Iteration 16)** — `GetEquityAsync` added to `IBacktestQueryService` interface and implemented in `BacktestQueryService`. Queries `EquitySnapshots` table with optional date range filter. Returns `EquityPoint[]` (TimestampUtc, Equity, Balance). The data already exists from `EquityPersistenceHandler` which saves snapshots during backtest runs.

**What's needed**: After each simulated fill/close in the replay adapter, emit an `AccountUpdate`
with updated floating PnL so `DrawdownTracker` and `EquityPersistenceHandler` accumulate a real curve.

---

### OBS-05 — No per-strategy performance breakdown
`BarEvaluations` is per-strategy per-bar, which is the raw data. But there's no aggregated view of:
- Strategy A: 4320 bars evaluated, 3 signals fired, 3 trades opened, 2 wins, 1 loss
- Strategy B: 4320 bars evaluated, 0 signals fired, 0 trades

The `BacktestQueryService` doesn't expose this. The Detail page doesn't show it.

---

## Part 5 — Backtest Architecture: How It Actually Works (and where it's broken)

### Current flow when you click "Run Backtest" in the UI

```
UI Run.cshtml
  → BacktestOrchestrator.Start()
    → generates RunId (8-char hex)
    → BacktestRunner.RunAsync(cfg) called as fire-and-forget
      → launches ctrader-cli as external Process (subprocess A)
        → ctrader-cli starts cBot inside its sandbox
        → cBot connects to engine via NetMQ
      → optionally launches TradingEngine.Host as external Process (subprocess B)
        → EngineWorker starts: SimulatedBrokerAdapter (not BacktestReplayAdapter)
        → ProcessBarsAsync, ProcessTicksAsync etc. start
        → cBot sends bars/ticks via NetMQ → engine evaluates strategies
        → Signals → OrderDispatcher → broker.SubmitOrderAsync → fill simulation
        → TradeClosed events → TradePersistenceHandler → DB
      → ctrader-cli exits
      → BacktestRunner reads report.json for NetProfit, MaxDD etc.
      → BacktestOrchestrator queries DB for trade stats (overrides report.json stats)
      → BacktestRuns record saved to DB
```

### What BacktestReplayAdapter is for (and why it's not connected to this flow)

`BacktestReplayAdapter` was created in Phase 4 as a **credential-free alternative** that reads bars
from the SQLite `Bars` table instead of running ctrader-cli. It is intended for:
- In-process integration tests (no cTrader credentials needed)
- Faster local development iteration
- CI/CD verification

**It is not wired into the UI flow at all.** The UI flow always uses ctrader-cli + engine subprocess.
The replay adapter exists in Infrastructure but is never registered in DI for any runtime path.

### What the flow should look like for a "pure engine" backtest

```
UI Run.cshtml
  → BacktestOrchestrator.Start()
    → generates RunId
    → starts engine in-process (not subprocess) using BacktestReplayAdapter
      → adapter reads bars from Bars table
      → engine evaluates strategies, fills orders at close prices
      → TradeClosed → DB with RunId
      → BarEvaluated → DB with RunId
    → on completion, saves BacktestRuns summary
```

This would be faster, credential-free, fully observable, and testable. The ctrader-cli path would
remain for "live-equivalent" backtesting that goes through the actual cBot.

### Key open question for next iteration

**Q: Should "Run Backtest" in the UI use the engine replay path or the ctrader-cli path?**

Options:
- A: Always ctrader-cli (current). Requires cTrader account. Harder to debug. Subprocess communication
     is opaque. This is the "production-equivalent" path.
- B: Engine replay only. Credential-free, fast, fully observable, no subprocess. Requires
     pre-loaded bars in the DB. Less representative of actual cBot execution.
- C: Both, selectable. UI has "Mode: Engine Replay / cTrader" toggle. Replay for development,
     cTrader for final verification.

Recommendation: **Option C**, with replay as the default during development. The engine replay
adapter already exists — it just needs BUG-01 and BUG-02 fixed and wired into the UI flow.

---

## Part 6 — Why Only 2–3 Trades in 3 Months (Root Cause Analysis)

The "only 2 trades" problem is multi-layered. Each layer on its own could explain zero trades.
All of them together make diagnosis very hard.

### Layer 1: Strategy filter was broken (partially fixed in Iteration 10)
`latestBar.Low <= currentPrice` was always true. The RSI gate was the only real filter.
RSI < 35 on H1 EURUSD occurs ~3–5 times per quarter. After the fix (0.2% proximity guard), signals
should increase but remain sparse.

### Layer 2: BacktestReplayAdapter never fills orders (BUG-01)
Even if signals fire, no position ever opens in the replay path. 0 trades regardless of signal count.

### Layer 3: No warmup data pre-loaded
`WarmUpIndicatorsAsync` is a no-op. The first `RequiredBarCount` bars (≈25 bars for MeanReversion)
produce no signal because the strategy returns null for insufficient bars. This is expected, but
the actual warmup period is invisible.

### Layer 4: BAR_EVAL log at Information floods the log, masking signal logs
With 4,000+ `Information` lines from `BAR_EVAL`, the `SIGNAL` lines are buried and hard to find.

### Layer 5: BarEvaluations not surfaced during the run
Even though `BarEvaluated` events are published, the 3-second flush delay and the UI's lack of
real-time display means you can't watch signal logic in action. You only see the final count.

### Layer 6: Other strategies evaluated but never signal
`TrendBreakout`, `EmaAlignment`, `SessionBreakout` may all have similar "always-true" conditions
or unreachable `RequiredBarCount` thresholds. Iteration 10 only fixed `MeanReversion`.

---

## Part 7 — Agent Programming Obstacles

These are issues that make implementing agents struggle or produce incorrect implementations.

### AGENT-01 — The backtest flow is unclear from reading the code alone
The three different "backtest" paths (ctrader-cli subprocess, engine subprocess, BacktestReplayAdapter)
are not documented anywhere. An agent reading `BacktestOrchestrator.cs` alone cannot determine which
path is active at runtime. Future implementing agents must read this document and understand that
`BacktestReplayAdapter` is not wired to the UI.

### AGENT-02 — Raw SQL in startup masks schema evolution
Schema changes made via `ctx.Database.ExecuteSqlRaw("ALTER TABLE...")` in `Web/Program.cs` and
`Host/Program.cs` are invisible to EF migration tooling. An agent adding a new column will add it
to the entity class, run `dotnet ef migrations add`, and get a migration that conflicts with the
already-applied raw SQL. This pattern must stop — all schema changes via EF migrations only.

### AGENT-03 — Test coverage doesn't verify end-to-end trade flow
Unit tests (87 passing) test individual components. The integration tests require cTrader credentials.
There is no automated test that verifies: "bar in → strategy evaluates → signal fires → order filled →
trade saved to DB". This is the most important path and it's not tested without credentials.
The `BacktestReplayAdapter` was created specifically to enable this test (Phase 4 of Iteration 10)
but the test was deferred. Until this test exists, regressions in the trade flow are invisible.

### AGENT-04 — `EngineRunContext` is in `TradingEngine.Domain` but has no domain significance
`EngineRunContext` is a pure infrastructure/operational concept (which process instance owns this run).
It has no business meaning. Placing it in the Domain project violates the layer boundary rule.
It should be in `TradingEngine.Host` or `TradingEngine.Services`.

### AGENT-05 — `BacktestOrchestrator` is still doing too much despite the CQRS split
After the Phase 5 refactor, it still holds `_runs` in-memory state, manages `BacktestRunState`,
queries the DB via `GetTradeStatsAsync`, and has a `BacktestRunState` record that is both a command
state object and a DTO used by the Progress page. The CQRS split was partial — the in-memory state
should move to the DB entirely.

---

## Part 8 — UI Redone: What's Needed

The current Razor Pages UI is adequate for displaying static DB data. It is not suitable for:
- Real-time backtest progress streaming
- Interactive equity curve with trade markers
- Per-bar signal audit browsing (thousands of rows)
- Side-by-side run comparison

### Recommended UI approach for next iteration

**Short term (Razor + htmx/Alpine.js)**: Add interactivity to existing pages without rewriting.
htmx can handle the SSE progress stream, dynamic table loading, and chart updates without a full SPA.
Keeps the server-side rendering advantage (simpler hosting, no CORS).

**Medium term (Blazor Server)**: If the team is C#-first, Blazor Server gives reactive UI with
full access to .NET domain types. SignalR connection allows server-push for live bar events.
The real-time equity curve update as bars replay becomes straightforward.

**Long term (separate React/Angular SPA)**: Better for complex charting (TradingView Lightweight
Charts, Chart.js), mobile, PWA. Requires a proper REST/WebSocket API layer. More infrastructure.

**Recommendation**: Blazor Server for this project. The data model is C# domain objects, the team
context is .NET-first, and Blazor Server's SignalR hub maps directly to the existing `IEventBus`
publish model.

### Backtest detail page: what must be shown

```
┌─ Backtest Run: abc12345 ─────────────────────────────────────────────────────┐
│ Symbol: EURUSD | Period: H1 | 2024-01-01 → 2024-03-31 | Balance: $100,000   │
│ AlgoHash: a3f9b2c1 | Strategies: mean-reversion, ema-alignment               │
├───────────────────────────────────────────────────────────────────────────────┤
│ EQUITY CURVE ──────────────────────────────────────────────────── [Chart.js] │
│  Trade markers (▲ long open, ▼ short open, × close) overlaid                │
├─────────────────┬─────────────────────────────────────────────────────────────┤
│ SUMMARY         │ TRADES                                                       │
│ Net P&L: +$340  │ # | Date | Dir | Lots | Entry | Exit | PnL | R | Reason    │
│ Trades: 4       │ 1 | Jan3 | L   | 0.01 | 1.095 | 1.098| +$30| 1.2| TP     │
│ Wins: 3         │ 2 | ...                                                      │
│ Win Rate: 75%   │                                                              │
│ Max DD: 0.8%    │                                                              │
├─────────────────┴─────────────────────────────────────────────────────────────┤
│ SIGNAL AUDIT (BarEvaluations)                                                  │
│ Strategy: [mean-reversion ▼]                                                  │
│ 4,320 bars | 12 signals fired | 4,308 rejected                                │
│ Rejection reasons: "not enough bars" x25, "RSI not extreme" x4280, "no signal" x3 │
│ [View all bar evaluations table — paginated]                                   │
├───────────────────────────────────────────────────────────────────────────────┤
│ PER-STRATEGY PERFORMANCE                                                       │
│ mean-reversion:  4 trades | 3W 1L | 75% WR | Avg R: 1.1                      │
│ ema-alignment:   0 trades | 0 signals fired                                    │
│ trend-breakout:  0 trades | 0 signals fired                                    │
└───────────────────────────────────────────────────────────────────────────────┘
```

---

## Part 9 — Recommended Priority Order for Next Iteration

Items ordered by: "what must be true before anything else is trustworthy"

1. **Fix BUG-01** (BacktestReplayAdapter fills orders) — nothing else matters if 0 trades always
2. **Fix BUG-02** (data loss for >2000 bars) — fundamental correctness
3. **Fix BUG-03** (force-close and exit reason) — all SL/TP stats are wrong until this is fixed
4. **Wire BacktestReplayAdapter into the UI flow** — makes the entire system credential-free and fast
5. **Fix DESIGN-05** (write BacktestRuns on start, update on completion) — fixes orphaned trade records
6. **Fix OBS-04** (emit AccountUpdate after each fill) — enables real equity curve
7. **Write the BacktestReplay E2E integration test** (was deferred in Iteration 10) — gates all fixes
8. **Fix BUG-04** (proper max drawdown) — now that the equity curve exists (step 6), use it
9. **Fix DESIGN-06** (BarEvaluationHandler shutdown drain) — prevent data loss on stop
10. **Add real-time signal event feed to Progress page** (OBS-02) — answers the "why no trades?" question
11. **Fix STD-03** (BAR_EVAL at Debug not Information) — make logs readable
12. **Per-strategy performance breakdown in Detail page** (OBS-05)
13. **Migrate from raw SQL schema changes to EF migrations** (AGENT-02)
14. **Fix BUG-05** (live cross-rates) — needed before any live trading

Items 1–7 together form a coherent "make backtest trustworthy" iteration.
Items 8–12 form "make backtest observable".
Items 13–14 are infrastructure hygiene.

---

## Minor Items (low urgency)

| ID | Description | File |
|----|-------------|------|
| MIN-01 | `WinRateLast20`/`AvgRLast20` never updated in `OnTradeResult` | `MeanReversionStrategy.cs:88` |
| MIN-02 | `SingleReader=true` missing on `BarEvaluationHandler` channel | `BarEvaluationHandler.cs:14` |
| MIN-03 | `WarmUpIndicatorsAsync` is a misleading no-op (just logs) | `EngineWorker.cs:366` |
| MIN-04 | `BuildBarSnapshot` allocates new `List<Bar>` per timeframe per bar | `EngineWorker.cs:328` |
| MIN-05 | `EngineRunContext` in Domain project (wrong layer) | `Domain/EngineRunContext.cs` |
| MIN-06 | `CancellationToken` missing on `RecomputeIndicatorsAsync`, `WarmUpIndicatorsAsync` | `EngineWorker.cs` |
| MIN-07 | `_processedExecutionIds` HashSet never pruned | `PositionTracker.cs:19` |
| MIN-08 | `DESIGN-03` — Cancel() doesn't kill subprocess | `BacktestOrchestrator.cs:102` |
| MIN-09 | `STD-01` — `await Task.CompletedTask` cargo-cult in 4 methods | multiple |
| MIN-10 | `STD-02` — `double` for price comparison in strategy | `MeanReversionStrategy.cs:55` |

---

## Iteration 27 Resolved Items (Web UI)

**Updated**: 2026-06-16. See `docs/iterations/iter-27/PLAN.md` (Parts A–E). All test suites green
(Unit 181/4-skip, Integration 35, Architecture 3, Simulation FTMO suite).

- **UI-01 — Live Monitor "stuck on connecting"** ✅ **Fixed (Iteration 27)**. `wwwroot/js/run-client.js`
  was an IIFE with **no ES export**, but `Monitor.cshtml` does `import { createRunClient }`. The named
  import threw at module load and aborted the whole `<script type="module">`, so SignalR never connected
  and the page froze on "Connecting to run…". Converted the file to a proper ES module (named export +
  a `window.createRunClient` back-compat alias).
- **UI-02 — Strategy picker ignored** ✅ **Fixed (Iteration 27)**. The New-Backtest strategy selection
  was carried to `BacktestController` (into `cfg.CustomParams["StrategyIds"]`) but never forwarded to the
  engine host, and `AddStrategiesFromOptions` hardcoded *all* configured strategies — so every run ran
  the whole bank regardless of the pick. Added `EngineHostOptions.ActiveStrategyIds`, threaded the
  selection from `BacktestOrchestrator`, and filter via `StrategyRegistry.SelectActiveIds`
  (empty = all configured). Covered by `Iter27FixTests.SelectActiveIds_*`.
- **UI-03 — Monitor funnel counters always 0** ✅ **Fixed (Iteration 27)**. `TallyEvent` matched event
  names the engine never emits (`FILL`/`REJECT`); remapped to `EXEC`/`CLOSE`/`REJECTED`/`BREACH`.
  Covered by `Iter27FixTests.TallyEvent_*`.
- **UI-04 — Report funnel inflated by per-bar noise** ✅ **Fixed (Iteration 27)**. Per-strategy Signals
  counted `BAR_EVAL`; now Signals = accepted orders + rejects (lifecycle `OrderSubmitted(Accepted)`,
  de-duped from the dispatcher). Logic extracted to `ReportModel.BuildFunnel` + covered by tests.
- **UI-05 — Report equity curve always empty** (was `OBS-04`) ✅ **Fixed (Iteration 27)**. The engine's
  intra-bar `AccountSnapshot`s live only in the inner host's in-memory store (disposed at end of run) and
  aren't visible to the separate Report request. The Report now derives a realized-equity curve from the
  run's trades (the same walk that computes MaxDd), so the chart and the MaxDd KPI are consistent by
  construction and end at `initialBalance + netPnL`. *Conscious deviation* from PLAN Decision D-Equity
  (DB persistence) per the "pick the smaller diff" rule — no schema migration needed.
- **UI-06 — Lifecycle decision records persisted with `RunId=""`** ✅ **Fixed (Iteration 27)**.
  `PipelineEventWriter.Record` stamps its own run id when the record carries none, so the Report funnel
  sees fills/closes. Covered by `Iter27FixTests.Record_stamps_writer_runId_when_record_runId_is_empty`.
- **UI-07 — Hardcoded `/api/performance` stub; Trade Detail balance from 0; sim clock date-only** ✅
  **Fixed (Iteration 27)** (carried from the prior session's Part A; verified building + behaving).

**Deferred / consciously left:** the SSE `/api/backtest/{runId}/stream` endpoint is now annotated as
legacy/unconsumed (the Monitor uses SignalR) rather than deleted, because `BacktestProgressStore` is
still woven into `BacktestJournal`. `BarsTotal` still ignores weekend/market gaps (display-only,
low priority).

---

## Part 10 — Rework: UI & Config Flexibility (open)

**Logged**: 2026-06-17. Backlog captured while deciding next direction, after the iter-29/iter-30 engine
fixes (indicator-key/regime correctness; breakeven/trailing wired). These five are *flexibility/UX*
features, not correctness bugs — they make the engine usable for real experimentation. Listed roughly in
dependency order; see the sequencing note at the end.

> **Cross-cutting prerequisite (not in the original list):** all of RW-03/04/05 are only as useful as the
> **data behind them**. Today the only bundled history is H1 EURUSD (bull/bear/ranging/ddcrash/maxdd) +
> USDJPY (bull) — no H4, no other symbols. A real multi-symbol / multi-timeframe data import is the
> implicit unblocker for batch sweeps, auto-mode, and global symbol selection to mean anything.

### RW-01 — Settings page: view/edit every tunable constant
**What**: A UI screen to inspect and edit the preset values currently spread across `config/*.json`
**and** the ones that exist only as C# defaults (invisible to anyone editing JSON).
**Why**: After iter-29/30 the number of knobs grew (regime thresholds, mean-reversion RSI/proximity,
per-strategy breakeven/trailing). Some are reachable from JSON; others are still code-only defaults, so a
JSON editor sees an incomplete picture.
**Where it lives today**: `config/strategies/*.json`, `config/risk-profiles/*.json`,
`config/prop-firms/*.json`, `config/regime.json`, `config/sizing-policy.json`, `config/governor.json`,
`config/rotation.json`, `config/symbols.json`; plus code-default records — `*Parameters`/`*Config` in
`src/TradingEngine.Strategies/*`, `PositionManagementOptions`/`SlOptions`/`TpOptions`/`TrailingOptions`/
`BreakevenOptions` (`src/TradingEngine.Domain/PositionManagement`), `RegimeOptions`.
**Notes / gotchas**:
- The goal is *surface every knob* — including defaults that the JSON omits (e.g. a strategy JSON that
  doesn't set `rsiOversold` silently inherits the record default). The page should show the *effective*
  value (JSON-or-default) and let you make it explicit.
- `config/playbook.json` and `config/position-management.json` are **dead** (never loaded) — decide
  whether to wire or delete before exposing them, or the settings page will surface phantom knobs.
- Validation must reuse `ConfigLoader`'s cross-reference checks (riskProfileId → risk-profiles,
  propFirmRuleSetId → prop-firms) so edits can't produce an unloadable config.

### RW-02 — Inherited / layered config for backtests
**What**: Let a run override/inherit from a base config — swap the prop-firm ruleset, risk profile, or a
handful of strategy params *on top of* a shared default, without duplicating whole files.
**Why**: Today changing one knob for one run means editing a global file or duplicating it. Layering
(`base → profile → per-run overrides`) is the backbone that RW-03 (sweeps) and RW-05 (symbols) need.
**Where it lives today**: `ConfigLoader.Load()` reads whole files into `LoadedConfig`;
`EngineHostOptions.PreloadedConfig`/`ActiveStrategyIds` already hint at per-run injection (added iter-27).
**Notes**: Define a small override/merge model (deep-merge JSON over the base `LoadedConfig`), thread it
through `EngineHostOptions`, and keep the merged result immutable per run. Watch param-record
deserialization (it's case-insensitive, no `Disallow`).

### RW-03 — Batch / multi-run backtest runner
**What**: Run many backtests in one go (across rulesets, symbols, timeframes, parameter sweeps) and
compare results in one view.
**Why**: The only way to actually evaluate the now-working strategies + BE/trailing settings.
**Depends on**: RW-02 (per-run overrides define each cell of the sweep).
**⚠️ Isolation gotcha (verify when this lands)**: the cross-run indicator-state leak was the
`SkenderIndicatorService` `bars.Count`-keyed cache — **removed in iter-29** (the service is now stateless;
per-bar de-dupe lives in `IndicatorSnapshotService`). So in-process batch runs no longer share indicator
values. **Still verify**: each run gets a fresh/`Reset()`-ed `IndicatorSnapshotService` + `TradingLoop`
(see `EngineRunner.ResetState`) and no other singleton carries state across runs (e.g. `PositionManager`
dictionaries, `CrossRateStore`). Add a regression test: two back-to-back runs with different data must not
influence each other.

### RW-04 — Auto strategy mode (regime-based / performance-based selection)
**What**: The deferred "auto-apply per regime" — pick/weight strategies automatically instead of running
the fixed `ActiveStrategyIds` set.
**Why**: The product feature behind "auto apply them … on nominated symbols/timeframes."
**Foundations now real**: regime detection actually works post-iter-29 (was always `Unknown`);
`StrategyBankService.GetActive` already *filters* by regime; `config/rotation.json` has a
`PerformanceBased` mode that is currently `Disabled`, with the hook in `StrategyBankService.NotifyResult`.
**Notes**: This is *selection/weighting*, a layer above the existing regime *filter*. Best built after
RW-03 so the selection rules can be validated against batch results rather than guessed.

### RW-05 — Global symbol selection, end-to-end
**What**: A single place to nominate which symbols the system trades, flowing through to strategies and
the data feed — instead of per-strategy `symbols` arrays + `ActiveStrategyIds` in `appsettings.json`.
**Where it lives today**: per-strategy `symbols` (JSON), `Engine.ActiveStrategyIds` (appsettings),
`HistoricalDataProvider.BuildPath` (keys data files by `{symbol}-{tf}`), `SymbolCatalog`/`symbols.json`.
**Notes**: Decide precedence (global selection ∩ per-strategy `symbols`?), and gate on data availability
(a symbol with no history just produces nothing). Overlaps with RW-01 (it's a config surface) and RW-02
(per-run symbol override).

### Sequencing note
RW-02 is the backbone (unblocks RW-03 and RW-05) and is contained backend work. RW-03 gives the most
analytical payoff and its one-time blocker (the cache leak) is already cleared. RW-01 can land
incrementally (start read-only, then editable). RW-04 should come last — it needs RW-03 to validate
selection rules. None of RW-03/04/05 pay off without real multi-symbol / multi-TF data, so a data import
is the highest-leverage *non-listed* prerequisite.
