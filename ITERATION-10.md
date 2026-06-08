# ITERATION 10 — Observability, Metadata, Event-Driven Persistence, and Rich Backtest UI

## New Session Context

This iteration assumes ITERATION-9 is complete:
- `diag` PUB/SUB channel added to cBot + `NetMQBrokerAdapter`
- `HistoryBars` parameter set in cBot; `GetBars(tf, symbol, count)` overload used
- `bar.OpenTime` UTC fix applied
- Dynamic port allocation (`PortHelper`) in pipeline tests

If ITERATION-9 items are NOT yet done, complete them first — this iteration relies on observability and correct bar counts being in place.

### Uncommitted Files (verify before starting)
Run `git status` and confirm the branch is clean. All ITERATION-9 work should be committed.

### Problem Summary in One Paragraph
The engine produces trades that cannot be traced back to a specific backtest run (RunId is stamped after the fact using a fragile time-range query). The backtest run record itself is missing critical metadata: algo hash, strategy parameters, risk profile, period, date range, initial balance. There is no signal audit trail — you cannot see why a strategy rejected a bar. The UI has no CQRS separation — `BacktestOrchestrator` conflates command (run backtest), state management, and query (list runs). Progress is polled from an in-memory queue that evaporates on server restart. The backtest viewer shows no equity curve, no trade drill-down, no strategy context. Two trades in a 3-month backtest is suspiciously low — the mean-reversion strategy condition has a bug.

---

## Mandatory Reading (for implementing agent)

Read these files before writing any code. The plan references exact line numbers and structures.

1. `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` — locate `StampTradesWithRunIdAsync` (lines 34–48) and `RunAsync` — this is the primary target for Phase 0
2. `src/TradingEngine.Domain/Interfaces/IBacktestRunRepository.cs` — `BacktestRunSummary` record is missing fields; note which fields are absent
3. `src/TradingEngine.Infrastructure/Persistence/Entities/BacktestRunEntity.cs` — entity has fields (Period, BacktestFrom, BacktestTo, InitialBalance) that `BacktestRunSummary` does NOT — they are never populated
4. `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteBacktestRunRepository.cs` — `SaveAsync` maps from `BacktestRunSummary` to entity; gaps are obvious
5. `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteTradeRepository.cs` — `SaveAsync` never sets `entity.RunId` (line 37 writes it as `null`)
6. `src/TradingEngine.Services/PersistenceService.cs` — uses `IServiceScopeFactory` correctly (no DbContext lifetime bug here); understand `SaveTradeAsync`
7. `src/TradingEngine.Host/EquityPersistenceHandler.cs` — the canonical event handler pattern; replicate it for trades and bar evaluations
8. `src/TradingEngine.Domain/Events/TradeClosed.cs` + `TradeOpened.cs` — events exist; confirm they carry `TradeResult` / `Position`
9. `src/TradingEngine.Web/Pages/Backtests/Index.cshtml.cs` — note `Period = ""` on line 38 (data loss from missing Summary field)
10. `src/TradingEngine.Web/Pages/Backtests/Run.cshtml.cs` — entry point for backtest command; note `BacktestConfig` fields
11. `src/TradingEngine.Strategies/MeanReversion/MeanReversionStrategy.cs` — line 55: `latestBar.Low <= currentPrice` — this condition is wrong (see Phase 9)
12. `src/TradingEngine.CTraderRunner/BacktestRunner.cs` — understand how `BacktestConfig` reaches the cBot subprocess; locate where RunId is generated
13. `src/TradingEngine.Host/EngineWorker.cs` — `ProcessBarsAsync` — locate where `OrderDispatcher.DispatchAsync` is called and where trade results would be published to event bus

---

## Context: Flaws Being Fixed

### Flaw 1 — RunId Attribution Is Broken
`StampTradesWithRunIdAsync` updates `trades WHERE ClosedAtUtc BETWEEN from AND to AND RunId IS NULL`. If two backtests overlap in time (even by accident — same date range, different strategies), trades get attributed to the wrong run. RunId must be written at trade-save time, not stamped after.

**Root cause chain**: `BacktestConfig` has no RunId field → `BacktestRunner` generates RunId internally → the RunId never reaches the engine → engine saves trades without RunId → `BacktestOrchestrator` tries to recover by time-range stamp.

**Fix**: Generate RunId in `BacktestOrchestrator.Start()` → pass in `BacktestConfig` → BacktestRunner passes it as an env var to the engine subprocess → engine reads it at startup → all trades saved with that RunId.

### Flaw 2 — BacktestRunSummary Missing Critical Fields
`BacktestRunSummary` (the domain record) is missing: `Period`, `BacktestFrom`, `BacktestTo`, `InitialBalance`. These exist in `BacktestRunEntity` but are never populated. The UI shows `Period = ""` for all persisted runs.

### Flaw 3 — No Signal Audit Trail
You cannot see which bar triggered a signal, which indicator values were present, or why a strategy rejected every bar. The `SIGNAL|` log line exists but is not persisted.

### Flaw 4 — UI Mixes Command and Query
`BacktestOrchestrator` is a singleton that: (a) runs backtests, (b) holds in-memory run state, (c) queries DB, (d) computes stats. The `Index.cshtml.cs` merges in-memory state with DB records, constructing an `BacktestOrchestrator.BacktestRunState` from a `BacktestRunSummary` — a direction violation (UI page model constructed from infrastructure record).

### Flaw 5 — Progress Is Lost on Restart
`LogLines` is a `ConcurrentQueue<string>` in memory. Server restart = all progress gone. `Progress.cshtml` polls an endpoint backed by this queue.

### Flaw 6 — Backtest Viewer Is Bare
No equity curve, no drawdown visualization, no per-trade detail, no side-by-side run comparison. UI shows only a summary table.

### Flaw 7 — No Algo Versioning
There is no way to tell which binary a backtest ran against. Running two backtests with different strategy code (different `.algo` build) looks identical in the DB. Stale `.cbotset` caches silently run old algo versions.

### Flaw 8 — Low Trade Count (Strategy Bug)
`MeanReversionStrategy.Evaluate` line 55:
```csharp
if (rsi < 30 && latestBar.Low <= currentPrice)   // BUG: always true (price is within bar range)
    dir = TradeDirection.Long;
else if (rsi > 70 && latestBar.High >= currentPrice)  // BUG: always true
    dir = TradeDirection.Short;
```
`latestBar.Low <= currentPrice` is almost always true (the current price is never below the bar's low). The condition adds no filtering — the only real gate is `rsi < 30`. For EURUSD H1, RSI<30 occurs ~2–5 times per quarter, which explains exactly 2 trades in 3 months.

The condition was likely intended to check whether price is **near** the extreme (e.g., `currentPrice <= latestBar.Low * 1.005m` for long). This is a strategy logic fix, not a config issue.

---

## Target Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  BacktestOrchestrator (COMMAND only)                        │
│  - Start(cfg) → generate RunId → set cfg.RunId              │
│  - passes RunId in BacktestConfig → BacktestRunner env var  │
│  - NO in-memory state, NO query methods                     │
└──────────────┬──────────────────────────────────────────────┘
               │ RunId flows down
               ▼
┌─────────────────────────────────────────────────────────────┐
│  Engine Host (receives RunId via Engine__RunId env var)     │
│  - EngineWorker reads RunId at startup                      │
│  - Publishes TradeClosed events with RunId attached         │
│  - Publishes BarEvaluated events                            │
├────────────────────────┬────────────────────────────────────┤
│ TradePersistenceHandler│ BarEvaluationHandler               │
│ IEventHandler<         │ IEventHandler<                     │
│  TradeClosed>          │  BarEvaluated>                     │
│ → saves TradeResult    │ → saves BarEvalEntity              │
│   with RunId set       │   (indicator values + signal)      │
└────────────────────────┴────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│  BacktestQueryService (QUERY only)                          │
│  - GetAllRunsAsync() → IReadOnlyList<BacktestRunView>       │
│  - GetRunDetailAsync(runId) → BacktestRunDetail             │
│  - GetTradesForRunAsync(runId) → IReadOnlyList<TradeView>   │
│  - GetEquityCurveAsync(runId) → IReadOnlyList<EquityPoint>  │
│  - GetBarEvaluationsAsync(runId) → audit trail              │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│  UI Pages (Razor)                                           │
│  - Backtests/Run.cshtml → calls IBacktestCommandService     │
│  - Backtests/Index.cshtml → calls IBacktestQueryService     │
│  - Backtests/Detail.cshtml → NEW: equity curve + trades     │
│  - Backtests/Compare.cshtml → NEW: side-by-side runs        │
│  - api/backtests/{runId}/stream → SSE endpoint (progress)   │
└─────────────────────────────────────────────────────────────┘
```

---

## Phase 0 — RunId Propagation (Foundational)

**This must be done first. Every other phase depends on RunId flowing correctly.**

### 0a. Add RunId to BacktestConfig

**File**: `src/TradingEngine.CTraderRunner/BacktestConfig.cs`

```csharp
public sealed record BacktestConfig
{
    public string RunId { get; init; } = "";   // ADD THIS
    public string Symbol { get; init; } = "EURUSD";
    public string Period { get; init; } = "h1";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public decimal Balance { get; init; } = 100_000;
}
```

### 0b. Generate RunId in BacktestOrchestrator.Start()

**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs`

In `Start(BacktestConfig cfg)`, before calling `RunAsync`:
```csharp
public BacktestRunState Start(BacktestConfig cfg)
{
    var runId = Guid.NewGuid().ToString("N")[..8];
    cfg = cfg with { RunId = runId };   // stamp RunId into config
    var state = new BacktestRunState { RunId = runId, Symbol = cfg.Symbol, Period = cfg.Period };
    _runs[runId] = state;
    // ...
    _ = RunAsync(runId, cfg);
    return state;
}
```

### 0c. Pass RunId as env var to engine subprocess

**File**: `src/TradingEngine.CTraderRunner/BacktestRunner.cs`

In `RunAsync`, when building `ProcessStartInfo` for the engine:
```csharp
psi.Environment["Engine__RunId"] = cfg.RunId;
```

### 0d. Engine reads RunId at startup

**File**: `src/TradingEngine.Host/Program.cs`

After reading `EngineMode`:
```csharp
var engineRunId = builder.Configuration["Engine:RunId"] ?? "";
builder.Services.AddSingleton(new EngineRunContext(engineRunId));
```

Create `src/TradingEngine.Host/EngineRunContext.cs`:
```csharp
namespace TradingEngine.Host;
public sealed record EngineRunContext(string RunId);
```

### 0e. TradeClosed event carries RunId

**File**: `src/TradingEngine.Domain/Events/TradeClosed.cs`

```csharp
public sealed record TradeClosed(TradeResult Result, string RunId, DateTime OccurredAtUtc) 
    : EngineEvent(OccurredAtUtc);
```

### 0f. EngineWorker publishes TradeClosed with RunId

**File**: `src/TradingEngine.Host/EngineWorker.cs`

Inject `EngineRunContext` in constructor. Find where `TradeClosed` is published (likely in `PositionTracker` or `ProcessBarsAsync` — read the code to locate it). Ensure the event is published with `_runContext.RunId`.

If trades are currently NOT published via event bus (only saved directly via PersistenceService), add the publish call:
```csharp
await _eventBus.PublishAsync(new TradeClosed(tradeResult, _runContext.RunId, _clock.UtcNow), ct);
```

### 0g. Remove StampTradesWithRunIdAsync

**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs`

Delete `StampTradesWithRunIdAsync` entirely (lines 34–48). Remove the call to it in `RunAsync`. This method is replaced by Phase 2 (TradePersistenceHandler).

**Verify Phase 0**: Run a backtest. Check `SELECT RunId FROM Trades` — all trades should have the RunId set (not null). If RunId is still null, the event is not being handled yet (Phase 2 will add the handler).

---

## Phase 1 — BacktestRunSummary and Entity Metadata Expansion

### 1a. Expand BacktestRunSummary

**File**: `src/TradingEngine.Domain/Interfaces/IBacktestRunRepository.cs`

Replace the current record:
```csharp
public sealed record BacktestRunSummary(
    string RunId,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    string Symbol,
    string Period,           // ADD
    DateTime BacktestFrom,   // ADD
    DateTime BacktestTo,     // ADD
    decimal InitialBalance,  // ADD
    string AlgoHash,         // ADD (see Phase 8)
    string StrategyParamsJson, // ADD (serialized strategy configs)
    decimal NetProfit,
    decimal MaxDrawdownPct,
    int TotalTrades,
    int WinningTrades,
    double WinRatePct,
    int ExitCode,
    string? ErrorMessage);
```

Update `IBacktestRunRepository` interface — no signature changes needed (record update is enough).

### 1b. Expand BacktestRunEntity

**File**: `src/TradingEngine.Infrastructure/Persistence/Entities/BacktestRunEntity.cs`

Add the missing fields (Period and BacktestFrom/To/InitialBalance already exist in entity — verify. Only add what's missing):
```csharp
public string AlgoHash { get; set; } = "";
public string StrategyParamsJson { get; set; } = "{}";
```

### 1c. Update SqliteBacktestRunRepository.SaveAsync

**File**: `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteBacktestRunRepository.cs`

Populate the now-mapped fields:
```csharp
var entity = new BacktestRunEntity
{
    RunId = run.RunId,
    StartedAtUtc = run.StartedAtUtc,
    CompletedAtUtc = run.CompletedAtUtc,
    Symbol = run.Symbol,
    Period = run.Period,                    // was missing
    BacktestFrom = run.BacktestFrom,        // was missing
    BacktestTo = run.BacktestTo,            // was missing
    InitialBalance = run.InitialBalance,    // was missing
    AlgoHash = run.AlgoHash,
    StrategyParamsJson = run.StrategyParamsJson,
    NetProfit = run.NetProfit,
    MaxDrawdownPct = run.MaxDrawdownPct,
    TotalTrades = run.TotalTrades,
    WinningTrades = run.WinningTrades,
    WinRatePct = run.WinRatePct,
    ExitCode = run.ExitCode,
    ErrorMessage = run.ErrorMessage,
};
```

Update `GetAllAsync` and `GetByIdAsync` projection to include the new fields.

### 1d. Pass full metadata when saving BacktestRunSummary

**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` — `RunAsync`

When calling `repo.SaveAsync(summary, ...)`, pass the full config fields:
```csharp
var summary = new BacktestRunSummary(
    result.RunId, state.StartedAt, DateTime.UtcNow,
    cfg.Symbol, cfg.Period,
    cfg.Start, cfg.End, cfg.Balance,
    algoHash,          // from Phase 8 — use "" for now
    strategyParams,    // serialize from IConfiguration — see Phase 8
    result.NetProfit, result.MaxDrawdownPct,
    result.TotalTrades, result.WinningTrades, result.WinRatePct,
    result.ExitCode, null);
```

### 1e. Add EF Core migration

```bash
cd src/TradingEngine.Infrastructure
dotnet ef migrations add AddBacktestMetadata --startup-project ../../src/TradingEngine.Web
dotnet ef database update --startup-project ../../src/TradingEngine.Web
```

**Verify Phase 1**: Run a backtest. Check `SELECT Symbol, Period, BacktestFrom, BacktestTo, InitialBalance FROM BacktestRuns` — all fields should be populated.

---

## Phase 2 — Event-Driven Trade Persistence

Replace `_persistence.SaveTradeAsync` (direct call) with an event handler that persists trades with their RunId.

### 2a. Create TradePersistenceHandler

**File**: `src/TradingEngine.Host/TradePersistenceHandler.cs` (new file)

Model exactly on `EquityPersistenceHandler`:
```csharp
namespace TradingEngine.Host;

public sealed class TradePersistenceHandler : IEventHandler<TradeClosed>, IAsyncDisposable
{
    private readonly PersistenceService _persistence;
    private readonly ILogger<TradePersistenceHandler> _logger;
    private readonly Channel<(TradeResult Trade, string RunId)> _channel =
        Channel.CreateBounded<(TradeResult, string)>(new BoundedChannelOptions(1_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false
        });
    private readonly Task _flushTask;
    private readonly CancellationTokenSource _cts = new();

    public TradePersistenceHandler(PersistenceService persistence, ILogger<TradePersistenceHandler> logger)
    {
        _persistence = persistence;
        _logger = logger;
        _flushTask = DrainAsync(_cts.Token);
    }

    public async Task HandleAsync(TradeClosed evt, CancellationToken ct)
    {
        await _channel.Writer.WriteAsync((evt.Result, evt.RunId), ct);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        await foreach (var (trade, runId) in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                await _persistence.SaveTradeAsync(trade, runId, ct);
                _logger.LogDebug("TRADE_SAVED|{TradeId}|RunId={RunId}", trade.Id, runId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to persist trade {TradeId}", trade.Id);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        _cts.Cancel();
        try { await _flushTask; } catch { }
        _cts.Dispose();
    }
}
```

### 2b. Add RunId parameter to PersistenceService.SaveTradeAsync

**File**: `src/TradingEngine.Services/PersistenceService.cs`

```csharp
public async Task SaveTradeAsync(TradeResult trade, string runId, CancellationToken ct)
{
    try
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
        await repo.SaveAsync(trade, runId, ct);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to save trade. TradeId={TradeId}", trade.Id);
    }
}
```

### 2c. Set RunId in SqliteTradeRepository.SaveAsync

**File**: `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteTradeRepository.cs`

Add `string runId` parameter to `SaveAsync` signature (update `ITradeRepository` interface too):
```csharp
public async Task SaveAsync(TradeResult trade, string runId, CancellationToken ct)
{
    var entity = new TradeResultEntity
    {
        // ... existing fields ...
        RunId = string.IsNullOrEmpty(runId) ? null : runId,   // SET HERE
    };
    db.Trades.Add(entity);
    await db.SaveChangesAsync(ct);
}
```

### 2d. Register TradePersistenceHandler in DI

**File**: `src/TradingEngine.Host/Program.cs`

```csharp
builder.Services.AddSingleton<TradePersistenceHandler>();
builder.Services.AddSingleton<IEventHandler<TradeClosed>>(sp => 
    sp.GetRequiredService<TradePersistenceHandler>());
// Register for IAsyncDisposable cleanup
builder.Services.AddHostedService<TradePersistenceHandlerHost>(); // see below
```

Or use the same `IAsyncDisposable` cleanup pattern as EquityPersistenceHandler — check how it's registered in `Program.cs` and replicate.

### 2e. Remove direct SaveTradeAsync calls

Search for all `_persistence.SaveTradeAsync` or `ITradeRepository.SaveAsync` calls in `EngineWorker` or `PositionTracker` — remove them. The event handler now owns trade persistence.

**Verify Phase 2**: Run a backtest. After completion: `SELECT Id, RunId FROM Trades WHERE RunId IS NOT NULL` — should return all trades with correct RunId. `StampTradesWithRunIdAsync` should no longer exist in the codebase.

---

## Phase 3 — BarEvaluated Signal Audit Trail

Every bar evaluation (including strategy rejections with reason) should be persisted. This is the primary debugging tool for "why no trades".

### 3a. Define BarEvaluated domain event

**File**: `src/TradingEngine.Domain/Events/BarEvaluated.cs` (new file)

```csharp
namespace TradingEngine.Domain;

public sealed record BarEvaluated(
    string RunId,
    Symbol Symbol,
    Timeframe Timeframe,
    DateTime BarOpenTimeUtc,
    string StrategyId,
    IReadOnlyDictionary<string, double> IndicatorValues,
    bool SignalFired,
    TradeDirection? SignalDirection,
    string Reason,
    DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);
```

### 3b. Create BarEvaluationEntity

**File**: `src/TradingEngine.Infrastructure/Persistence/Entities/BarEvaluationEntity.cs` (new file)

```csharp
namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class BarEvaluationEntity
{
    public Guid Id { get; set; }
    public string RunId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "";
    public DateTime BarOpenTimeUtc { get; set; }
    public string StrategyId { get; set; } = "";
    public string IndicatorValuesJson { get; set; } = "{}";
    public bool SignalFired { get; set; }
    public string? SignalDirection { get; set; }
    public string Reason { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; }
}
```

### 3c. Register BarEvaluationEntity in DbContext

**File**: `src/TradingEngine.Infrastructure/Persistence/TradingDbContext.cs`

```csharp
public DbSet<BarEvaluationEntity> BarEvaluations => Set<BarEvaluationEntity>();
```

Add mapping in `OnModelCreating`:
```csharp
modelBuilder.Entity<BarEvaluationEntity>(e =>
{
    e.ToTable("BarEvaluations");
    e.HasKey(x => x.Id);
    e.HasIndex(x => x.RunId);
    e.HasIndex(x => new { x.RunId, x.StrategyId, x.BarOpenTimeUtc });
});
```

### 3d. Publish BarEvaluated from EngineWorker

**File**: `src/TradingEngine.Host/EngineWorker.cs` — `ProcessBarsAsync`

After each strategy evaluates, publish the event. This goes inside the `foreach (var strategy in _strategies)` loop, after `strategy.Evaluate(context)`:

```csharp
var intent = strategy.Evaluate(context);

// Publish BarEvaluated regardless of signal outcome
await _eventBus.PublishAsync(new BarEvaluated(
    _runContext.RunId,
    bar.Symbol,
    bar.Timeframe,
    bar.OpenTimeUtc,
    strategy.Id,
    new Dictionary<string, double>(_reusableIndicatorDict),
    signalFired: intent is not null,
    signalDirection: intent?.Direction,
    reason: intent?.Reason ?? "no signal",
    _clock.UtcNow), ct);
```

### 3e. Create BarEvaluationHandler

**File**: `src/TradingEngine.Host/BarEvaluationHandler.cs` (new file)

```csharp
namespace TradingEngine.Host;

public sealed class BarEvaluationHandler : IEventHandler<BarEvaluated>, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BarEvaluationHandler> _logger;
    private readonly Channel<BarEvaluated> _channel =
        Channel.CreateBounded<BarEvaluated>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,  // audit trail; lose old before blocking
            SingleWriter = false
        });
    private readonly Task _flushTask;
    private readonly CancellationTokenSource _cts = new();

    public BarEvaluationHandler(IServiceScopeFactory scopeFactory, ILogger<BarEvaluationHandler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _flushTask = FlushLoopAsync(_cts.Token);
    }

    public async Task HandleAsync(BarEvaluated evt, CancellationToken ct)
    {
        // Fire-and-forget into channel; never await channel write inline — too slow for per-bar path
        _channel.Writer.TryWrite(evt);
        await Task.CompletedTask;
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var buffer = new List<BarEvaluated>(500);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3_000, ct);
                buffer.Clear();
                while (_channel.Reader.TryRead(out var evt) && buffer.Count < 500)
                    buffer.Add(evt);
                if (buffer.Count == 0) continue;

                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                foreach (var evt in buffer)
                {
                    db.BarEvaluations.Add(new BarEvaluationEntity
                    {
                        Id = Guid.NewGuid(),
                        RunId = evt.RunId,
                        Symbol = evt.Symbol.Value,
                        Timeframe = evt.Timeframe.ToString(),
                        BarOpenTimeUtc = evt.BarOpenTimeUtc,
                        StrategyId = evt.StrategyId,
                        IndicatorValuesJson = System.Text.Json.JsonSerializer.Serialize(evt.IndicatorValues),
                        SignalFired = evt.SignalFired,
                        SignalDirection = evt.SignalDirection?.ToString(),
                        Reason = evt.Reason,
                        OccurredAtUtc = evt.OccurredAtUtc,
                    });
                }
                await db.SaveChangesAsync(ct);
                _logger.LogDebug("BAR_EVAL_FLUSHED|{Count}", buffer.Count);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BarEvaluationHandler flush failed");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        _cts.Cancel();
        try { await _flushTask; } catch { }
        _cts.Dispose();
    }
}
```

### 3f. Add EF Core migration for BarEvaluations table

```bash
dotnet ef migrations add AddBarEvaluations --startup-project ../../src/TradingEngine.Web
dotnet ef database update --startup-project ../../src/TradingEngine.Web
```

**Verify Phase 3**: Run a backtest. Check `SELECT COUNT(*) FROM BarEvaluations WHERE RunId = 'xxx'` — should return one row per bar per strategy. Check `SELECT SignalFired, Reason, IndicatorValuesJson FROM BarEvaluations WHERE SignalFired = 1` — should show the signal bar with RSI value.

---

## Phase 4 — BacktestReplayAdapter (Credentialless E2E with DB Assertions)

Currently E2E tests require cTrader credentials and a live cBot. A replay adapter reads bars from the SQLite `Bars` table (already saved during prior backtests) and feeds them into the NetMQ bus, allowing E2E testing without any cTrader connection.

### 4a. Create BacktestReplayAdapter

**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` (new file)

```csharp
namespace TradingEngine.Infrastructure.Adapters;

public sealed class BacktestReplayAdapter : IBrokerAdapter, IAsyncDisposable
{
    private readonly IBarRepository _barRepo;
    private readonly Symbol _symbol;
    private readonly Timeframe _timeframe;
    private readonly DateTime _from;
    private readonly DateTime _to;

    private readonly Channel<Tick> _tickChannel =
        Channel.CreateBounded<Tick>(new BoundedChannelOptions(10_000)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<Bar> _barChannel =
        Channel.CreateBounded<Bar>(new BoundedChannelOptions(2_000)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<AccountUpdate> _accountChannel =
        Channel.CreateBounded<AccountUpdate>(new BoundedChannelOptions(100)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<ExecutionEvent> _executionChannel =
        Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1_000)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });

    public ChannelReader<Tick> TickStream => _tickChannel.Reader;
    public ChannelReader<Bar> BarStream => _barChannel.Reader;
    public ChannelReader<AccountUpdate> AccountStream => _accountChannel.Reader;
    public ChannelReader<ExecutionEvent> ExecutionStream => _executionChannel.Reader;

    // ... implement ConnectAsync to load bars and replay them
    // ... implement GetAccountStateAsync to return initial balance
    // ... implement PlaceOrderAsync to immediately simulate fill
    // ... implement ClosePositionAsync to immediately simulate close
}
```

The adapter loads all bars from the DB for the given symbol/timeframe/date range and writes them to the bar channel. For each bar, it also generates a synthetic tick at the bar's close price. Orders are immediately filled at the close price (simplest valid simulation).

### 4b. E2E test using BacktestReplayAdapter

**File**: `tests/TradingEngine.Tests.Simulation/Pipeline/BacktestReplayTest.cs` (new file)

```csharp
[Fact(Timeout = 60_000)]
public async Task ReplayBacktest_PersistsTradesToDb_WithCorrectRunId()
{
    // Arrange: spin up engine in-process with BacktestReplayAdapter
    // No cTrader credentials needed
    var runId = Guid.NewGuid().ToString("N")[..8];
    var services = new ServiceCollection();
    // ... register all engine services
    // ... register BacktestReplayAdapter as IBrokerAdapter
    // ... register EngineRunContext(runId)

    using var provider = services.BuildServiceProvider();
    var worker = provider.GetRequiredService<EngineWorker>();

    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await worker.StartAsync(cts.Token);
    await Task.Delay(5000, cts.Token); // let backtest run
    await worker.StopAsync(CancellationToken.None);

    // Assert: DB has trades with correct RunId
    using var scope = provider.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
    var trades = await db.Trades.Where(t => t.RunId == runId).ToListAsync();
    trades.Should().NotBeEmpty("replay should produce at least one trade");

    // Assert: BarEvaluations saved
    var evals = await db.BarEvaluations.Where(e => e.RunId == runId).ToListAsync();
    evals.Should().NotBeEmpty("every bar should produce a BarEvaluated record");
}
```

**Verify Phase 4**: Run the replay test without any cTrader env vars. It should pass and show trades in the DB with RunId set.

---

## Phase 5 — UI CQRS Split

### 5a. Define IBacktestCommandService

**File**: `src/TradingEngine.Web/Services/IBacktestCommandService.cs` (new file)

```csharp
namespace TradingEngine.Web.Services;

public interface IBacktestCommandService
{
    Task<string> StartAsync(BacktestConfig cfg, CancellationToken ct);
    void Cancel(string runId);
}
```

### 5b. Define IBacktestQueryService

**File**: `src/TradingEngine.Web/Services/IBacktestQueryService.cs` (new file)

```csharp
namespace TradingEngine.Web.Services;

public sealed record BacktestRunView(
    string RunId, DateTime StartedAt, string Status,
    string Symbol, string Period, DateTime BacktestFrom, DateTime BacktestTo,
    decimal InitialBalance, decimal NetProfit, decimal MaxDrawdownPct,
    int TotalTrades, int WinningTrades, double WinRatePct,
    string AlgoHash, string? Error);

public sealed record EquityPoint(DateTime TimestampUtc, decimal Equity, decimal Balance);

public interface IBacktestQueryService
{
    Task<IReadOnlyList<BacktestRunView>> GetAllRunsAsync(CancellationToken ct);
    Task<BacktestRunView?> GetRunAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<TradeResult>> GetTradesAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<EquityPoint>> GetEquityCurveAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<BarEvaluated>> GetBarEvaluationsAsync(string runId, string strategyId, CancellationToken ct);
}
```

### 5c. Implement BacktestQueryService

**File**: `src/TradingEngine.Web/Services/BacktestQueryService.cs` (new file)

Reads from `IBacktestRunRepository`, `ITradeRepository`, `IEquityRepository`, and `BarEvaluations` DB table. No in-memory state.

### 5d. Refactor BacktestOrchestrator into BacktestCommandService

Keep `BacktestOrchestrator` as the implementation of `IBacktestCommandService` for now (rename later). Remove all query methods (`GetAll`, `GetState`). Remove `BacktestRunState.GetLogs()` from the query path.

Progress publishing must now go through a separate mechanism — see Phase 6.

### 5e. Update UI pages

- `Run.cshtml.cs`: inject `IBacktestCommandService` instead of `BacktestOrchestrator`
- `Index.cshtml.cs`: inject `IBacktestQueryService` instead of `BacktestOrchestrator + IBacktestRunRepository`. Remove the awkward merge of `BacktestRunState` and `BacktestRunSummary`.
- `Progress.cshtml.cs`: inject `IBacktestQueryService` to show current run status (read from DB, not in-memory)

**Verify Phase 5**: Build succeeds. Navigating to `/Backtests` shows all persisted runs. Running a new backtest redirects to progress page.

---

## Phase 6 — Real-Time Progress via SSE

### 6a. Create BacktestProgressStore

**File**: `src/TradingEngine.Web/Services/BacktestProgressStore.cs` (new file)

```csharp
namespace TradingEngine.Web.Services;

public sealed class BacktestProgressStore
{
    private readonly ConcurrentDictionary<string, Channel<string>> _channels = new();

    public ChannelWriter<string> GetWriter(string runId)
    {
        var ch = _channels.GetOrAdd(runId, _ =>
            Channel.CreateBounded<string>(new BoundedChannelOptions(500)
            { FullMode = BoundedChannelFullMode.DropOldest }));
        return ch.Writer;
    }

    public ChannelReader<string>? GetReader(string runId) =>
        _channels.TryGetValue(runId, out var ch) ? ch.Reader : null;

    public void Complete(string runId)
    {
        if (_channels.TryRemove(runId, out var ch))
            ch.Writer.TryComplete();
    }
}
```

### 6b. Add SSE endpoint

**File**: `src/TradingEngine.Web/Controllers/BacktestStreamController.cs` (new file)

```csharp
[Route("api/backtests/{runId}/stream")]
public sealed class BacktestStreamController(BacktestProgressStore store) : Controller
{
    [HttpGet]
    public async Task StreamAsync(string runId, CancellationToken ct)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("X-Accel-Buffering", "no");

        var reader = store.GetReader(runId);
        if (reader is null)
        {
            await Response.WriteAsync("data: {\"status\":\"not_found\"}\n\n", ct);
            return;
        }

        await foreach (var line in reader.ReadAllAsync(ct))
        {
            await Response.WriteAsync($"data: {line}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
        await Response.WriteAsync("data: {\"status\":\"done\"}\n\n", ct);
    }
}
```

### 6c. Wire BacktestCommandService to push progress

In `BacktestCommandService.RunAsync`, replace `state.LogLines.Enqueue(...)` with:
```csharp
_progressStore.GetWriter(runId).TryWrite(JsonSerializer.Serialize(new { type = "log", msg = "..." }));
```

Push structured events (log lines, bar count updates, final result).

### 6d. Update Progress.cshtml to use EventSource

Replace the current polling JavaScript with:
```javascript
const evtSource = new EventSource(`/api/backtests/${runId}/stream`);
evtSource.onmessage = (e) => {
    const data = JSON.parse(e.data);
    if (data.type === 'log') appendLog(data.msg);
    if (data.status === 'done') evtSource.close();
};
```

**Verify Phase 6**: Start a backtest. Open the Progress page. Log lines appear in real time without polling.

---

## Phase 7 — Rich Backtest Viewer

### 7a. Backtest Detail page

**File**: `src/TradingEngine.Web/Pages/Backtests/Detail.cshtml.cs` (new file)

Inject `IBacktestQueryService`. Load:
- Run metadata (symbol, period, dates, initial balance, algo hash)
- Equity curve data for Chart.js
- Trade list with entry/exit bar time + R-multiple + P&L
- BarEvaluations summary: total bars evaluated, bars with signal, rejection breakdown by reason

### 7b. Equity curve chart

In `Detail.cshtml`, add Chart.js line chart:
```html
<canvas id="equityChart"></canvas>
<script src="https://cdn.jsdelivr.net/npm/chart.js"></script>
<script>
    const data = @Json.Serialize(Model.EquityCurve);
    new Chart(document.getElementById('equityChart'), {
        type: 'line',
        data: {
            labels: data.map(p => p.timestampUtc),
            datasets: [{
                label: 'Equity',
                data: data.map(p => p.equity),
                borderColor: 'rgb(75, 192, 192)',
            }]
        }
    });
</script>
```

### 7c. Trade drill-down

Each trade in the trade list links to the specific bar evaluation row: "Why did this trade trigger?" — shows the indicator values from `BarEvaluations` at that bar's open time.

### 7d. Run comparison page

**File**: `src/TradingEngine.Web/Pages/Backtests/Compare.cshtml.cs` (new file)

Accept two `runId` query params. Show side-by-side:
- Equity curves overlaid on a single chart
- Summary stats table (net profit, max DD, trade count, win rate, algo hash)
- Different strategy params JSON diffs

**Verify Phase 7**: Navigate to `/Backtests/Detail?runId=xxx`. Equity curve renders. Trade list shows R-multiples. "Why triggered?" link works.

---

## Phase 8 — Algo Versioning

### 8a. Hash .algo binary in BacktestRunner

**File**: `src/TradingEngine.CTraderRunner/BacktestRunner.cs`

Before launching `ctrader-cli`, compute SHA256 of the `.algo` file:
```csharp
private static string ComputeAlgoHash(string algoPath)
{
    if (!File.Exists(algoPath)) return "missing";
    using var sha = System.Security.Cryptography.SHA256.Create();
    using var fs = File.OpenRead(algoPath);
    return Convert.ToHexString(sha.ComputeHash(fs))[..16].ToLowerInvariant();
}
```

Pass this hash in `BacktestResult`:
```csharp
// BacktestResult record — add:
public string AlgoHash { get; init; } = "";
```

Surface the hash in `BacktestOrchestrator.RunAsync` when building `BacktestRunSummary`.

### 8b. Display AlgoHash in UI

In `Detail.cshtml` and `Index.cshtml`, show the first 8 chars of `AlgoHash` as a badge next to the run. If two runs have different hashes, the comparison page highlights this.

### 8c. Serialize strategy params snapshot

**File**: `src/TradingEngine.Web/Services/BacktestCommandService.cs`

Before starting the backtest, serialize the strategy configs from `IConfiguration` to JSON and store in `BacktestRunSummary.StrategyParamsJson`. This gives a complete snapshot of what parameters were active at run time.

**Verify Phase 8**: Run two backtests back-to-back. `SELECT AlgoHash FROM BacktestRuns` should show the same hash (same binary). After rebuilding the .algo, rerun — hash should differ.

---

## Phase 9 — Strategy Logic Fix (Trade Count Investigation)

### 9a. Fix MeanReversion condition

**File**: `src/TradingEngine.Strategies/MeanReversion/MeanReversionStrategy.cs` — lines 55–58

The current condition:
```csharp
if (rsi < 30 && latestBar.Low <= currentPrice)     // latestBar.Low <= currentPrice is ALWAYS true
    dir = TradeDirection.Long;
else if (rsi > 70 && latestBar.High >= currentPrice)  // latestBar.High >= currentPrice is ALWAYS true
    dir = TradeDirection.Short;
```

Replace with a price-proximity guard. The intent of the condition is "price is near the extreme" — check that close is within 0.2% of the bar's low/high:
```csharp
var nearLow = (double)(latestBar.Close - latestBar.Low) / (double)latestBar.Close < 0.002;
var nearHigh = (double)(latestBar.High - latestBar.Close) / (double)latestBar.Close < 0.002;

if (rsi < 30 && nearLow)
    dir = TradeDirection.Long;
else if (rsi > 70 && nearHigh)
    dir = TradeDirection.Short;
```

This adds a meaningful price-location filter without being overly restrictive.

### 9b. Tune RSI thresholds

The default RSI thresholds of 30/70 are very strict for H1 EURUSD. Consider making them configurable parameters:
```csharp
public sealed record MeanReversionParameters
{
    public int RsiPeriod { get; init; } = 14;
    public double RsiOversold { get; init; } = 35;    // was hardcoded 30
    public double RsiOverbought { get; init; } = 65;  // was hardcoded 70
    // ...
}
```

Update `MeanReversionStrategy.Evaluate` to use `p.RsiOversold` / `p.RsiOverbought`.

### 9c. Investigate other strategies

Read `TrendBreakoutStrategy.cs`, `EmaAlignmentStrategy.cs`, and `SessionBreakoutStrategy.cs`. For each:
- What is `RequiredBarCount`? If > 34, they never evaluated before the ITERATION-9 bar-history fix.
- Do they have similar "always true" conditions?

Log rejection reasons to BarEvaluations (Phase 3) — this will definitively show why each strategy rejects bars.

**Verify Phase 9**: Run a 3-month backtest after the fix. `SELECT COUNT(*) FROM BarEvaluations WHERE SignalFired = 1` should be > 5 (many more signals than before).

---

## Verification Sequence

Run in order. Each step is a prerequisite for the next.

```bash
# 1. Build
dotnet build --no-incremental

# 2. Unit tests
dotnet test tests\TradingEngine.Tests.Unit --no-build

# 3. DB migration applied (requires running Web project)
dotnet ef database update --startup-project src\TradingEngine.Web --project src\TradingEngine.Infrastructure

# 4. Replay adapter E2E test (no cTrader credentials needed)
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "BacktestReplay"

# 5. Full pipeline test (requires cTrader credentials)
set CTrader__CtId=your-ctid
set CTrader__PwdFile=C:\path\to\ctrader.pwd
set CTrader__Account=your-account
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "ThreeDays"

# 6. Verify DB after pipeline test
# Connect to the SQLite DB and check:
# SELECT RunId, Symbol, Period, TotalTrades, AlgoHash FROM BacktestRuns ORDER BY StartedAtUtc DESC LIMIT 5;
# SELECT COUNT(*), RunId FROM Trades GROUP BY RunId;
# SELECT COUNT(*), SignalFired, StrategyId FROM BarEvaluations GROUP BY SignalFired, StrategyId;
```

---

## Critical Files

| File | Change | Phase |
|------|--------|-------|
| `src/TradingEngine.CTraderRunner/BacktestConfig.cs` | Add `RunId` field | 0 |
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Generate RunId in Start(); remove StampTradesWithRunIdAsync | 0 |
| `src/TradingEngine.CTraderRunner/BacktestRunner.cs` | Pass RunId as Engine__RunId env var; compute AlgoHash | 0, 8 |
| `src/TradingEngine.Host/Program.cs` | Register EngineRunContext; register new handlers | 0, 2, 3 |
| `src/TradingEngine.Host/EngineRunContext.cs` | NEW — holds RunId for current engine session | 0 |
| `src/TradingEngine.Domain/Events/TradeClosed.cs` | Add RunId parameter | 0 |
| `src/TradingEngine.Domain/Events/BarEvaluated.cs` | NEW domain event | 3 |
| `src/TradingEngine.Domain/Interfaces/IBacktestRunRepository.cs` | Expand BacktestRunSummary record | 1 |
| `src/TradingEngine.Infrastructure/Persistence/Entities/BacktestRunEntity.cs` | Add AlgoHash, StrategyParamsJson | 1 |
| `src/TradingEngine.Infrastructure/Persistence/Entities/BarEvaluationEntity.cs` | NEW entity | 3 |
| `src/TradingEngine.Infrastructure/Persistence/TradingDbContext.cs` | Add BarEvaluations DbSet | 3 |
| `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteBacktestRunRepository.cs` | Populate all entity fields | 1 |
| `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteTradeRepository.cs` | Set RunId on save | 2 |
| `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` | NEW credentialless replay adapter | 4 |
| `src/TradingEngine.Services/PersistenceService.cs` | Add runId param to SaveTradeAsync | 2 |
| `src/TradingEngine.Host/TradePersistenceHandler.cs` | NEW event handler | 2 |
| `src/TradingEngine.Host/BarEvaluationHandler.cs` | NEW event handler | 3 |
| `src/TradingEngine.Host/EngineWorker.cs` | Inject EngineRunContext; publish TradeClosed + BarEvaluated events | 0, 2, 3 |
| `src/TradingEngine.Web/Services/IBacktestCommandService.cs` | NEW interface | 5 |
| `src/TradingEngine.Web/Services/IBacktestQueryService.cs` | NEW interface + view models | 5 |
| `src/TradingEngine.Web/Services/BacktestQueryService.cs` | NEW implementation | 5 |
| `src/TradingEngine.Web/Services/BacktestProgressStore.cs` | NEW SSE channel store | 6 |
| `src/TradingEngine.Web/Controllers/BacktestStreamController.cs` | NEW SSE endpoint | 6 |
| `src/TradingEngine.Web/Pages/Backtests/Index.cshtml.cs` | Use IBacktestQueryService | 5 |
| `src/TradingEngine.Web/Pages/Backtests/Run.cshtml.cs` | Use IBacktestCommandService | 5 |
| `src/TradingEngine.Web/Pages/Backtests/Progress.cshtml.cs` | Use SSE EventSource | 6 |
| `src/TradingEngine.Web/Pages/Backtests/Detail.cshtml(.cs)` | NEW detail page with equity curve | 7 |
| `src/TradingEngine.Web/Pages/Backtests/Compare.cshtml(.cs)` | NEW comparison page | 7 |
| `src/TradingEngine.Strategies/MeanReversion/MeanReversionStrategy.cs` | Fix always-true condition | 9 |
| `src/TradingEngine.Strategies/MeanReversion/MeanReversionConfig.cs` | Add RSI threshold params | 9 |
| `tests/TradingEngine.Tests.Simulation/Pipeline/BacktestReplayTest.cs` | NEW credentialless E2E test | 4 |
| `docs/ITERATION-10-HANDOVER.md` | Write at end | — |

---

## Decisions to Record

| ID | Decision |
|----|----------|
| D81 | RunId generated in `BacktestOrchestrator.Start()` and propagated via `BacktestConfig.RunId` + `Engine__RunId` env var. Reason: RunId must flow from orchestrator → runner → engine at process start so trades are tagged at write time, not post-hoc. |
| D82 | `StampTradesWithRunIdAsync` removed permanently. Any future concurrent backtest overlap would have corrupted attribution. The correct fix is D81. |
| D83 | `BarEvaluated` events use `BoundedChannelFullMode.DropOldest` with capacity 50,000. Reason: audit trail data is lower priority than trade data; dropping oldest during a spike is acceptable. |
| D84 | `BacktestReplayAdapter` reads from `Bars` table (already persisted by cBot). It does NOT re-fetch from cTrader API. Reason: E2E tests must be credential-free and deterministic. |
| D85 | CQRS split: `BacktestOrchestrator` becomes `IBacktestCommandService` only. `IBacktestQueryService` reads exclusively from DB (no in-memory state). Reason: in-memory state is lost on server restart; DB is the source of truth. |
| D86 | Real-time progress uses SSE (not WebSocket). Reason: SSE is unidirectional (server → client), simpler, no connection upgrade needed, works through reverse proxies. WebSocket adds bidirectional overhead we don't need. |
| D87 | MeanReversion `nearLow`/`nearHigh` threshold set to 0.2% (0.002). This is a starting point — make it a `MeanReversionParameters` field if it needs tuning. |
| D88 | `AlgoHash` is the first 16 hex chars of SHA256 of the `.algo` binary. Full hash would work too; 16 chars is sufficient for detecting binary changes and is display-friendly. |

---

## Handover Notes

At the end of this iteration, write `docs/ITERATION-10-HANDOVER.md` with:
- Which phases were completed
- DB state: table row counts after a successful test run (BacktestRuns, Trades, BarEvaluations)
- Trade count before/after strategy fix (e.g., "2 → 12 trades in 3-month EURUSD H1")
- Any deferred phases with reason
- Failing tests or known issues
