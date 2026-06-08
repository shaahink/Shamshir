# Iteration 13 — Observability Pass

**Branch**: `iter/13-observability`
**Fixes**: OBS-01, OBS-02, OBS-03, OBS-05, STD-03, DESIGN-07 from `docs/OPEN-ISSUES.md`
**Depends on**: Iteration 12 — UI backtest produces > 0 trades
**Blocks**: Iterations 14 and 15 (both can start after this)

**Gate**: A replay backtest in the UI produces trades AND `BarEvaluations` count > 0 in DB.

---

## Read first

- `docs/agents/HOW-TO-WORK.md`
- `src/TradingEngine.Host/EngineWorker.cs` — line 199 (BAR_EVAL level), lines 210–263 (strategy loop)
- `src/TradingEngine.Host/BarEvaluationHandler.cs` — flush loop and DisposeAsync
- `src/TradingEngine.Web/Pages/Backtests/Progress.cshtml` — existing SSE JS
- `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` — progress pipeline

---

## What is missing

1. `BAR_EVAL` logged at `Information` floods log with 4,000+ lines per H1 backtest (STD-03)
2. `BarEvaluationHandler.DisposeAsync` silently drops events still in the channel on shutdown (DESIGN-07)
   — small backtests (< 3s) have zero `BarEvaluations` in DB because the 3-second flush timer
   never fires before the host stops. The strategy breakdown query returns nothing.
3. Progress page shows raw log lines only — no structure, no live counters (OBS-01, OBS-02)
4. Detail page shows no per-strategy performance (OBS-03)
5. No way to know during a run how many bars have been processed (OBS-05)

---

## Files to change

| File | Change | Phase |
|------|--------|-------|
| `src/TradingEngine.Host/EngineWorker.cs` | Fix log level; add `IProgress<>` optional param | A |
| `src/TradingEngine.Host/BarEvaluationHandler.cs` | Fix DisposeAsync to flush remaining events | A |
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Pass `IProgress<>` to inner host; subscribe | B |
| `src/TradingEngine.Web/Services/IBacktestQueryService.cs` | Add `GetStrategyBreakdownAsync` | C |
| `src/TradingEngine.Web/Services/BacktestQueryService.cs` | Implement breakdown query | C |
| `src/TradingEngine.Web/Pages/Backtests/Detail.cshtml` | Add per-strategy table | C |
| `src/TradingEngine.Web/Pages/Backtests/Detail.cshtml.cs` | Bind breakdown data | C |
| `src/TradingEngine.Web/Pages/Backtests/Progress.cshtml` | Color-coded event log; live counters | D |

**Do NOT touch**: `TradePersistenceHandler.cs`, `BacktestReplayAdapter.cs`, ctrader path in
`BacktestRunner.cs`, any migration.

---

## Phase A — Fix flush and log level

### A1 — BAR_EVAL log level (EngineWorker.cs line 199)

Find the exact line:
```csharp
_logger.LogInformation("BAR_EVAL|{Symbol}|{Tf}|openTime=...
```

Change `LogInformation` to `LogDebug`. One line change only.

### A2 — IProgress optional parameter in EngineWorker

**EngineWorker.cs constructor** — add one optional parameter at the end (same pattern as
`DataFeedService? dataFeed = null`):

```csharp
public EngineWorker(
    IBrokerAdapter broker,
    IRiskManager riskManager,
    DrawdownTracker drawdownTracker,
    IEnumerable<IStrategy> strategies,
    IIndicatorService indicators,
    IEventBus eventBus,
    IEngineClock clock,
    ISymbolInfoRegistry symbolRegistry,
    IRiskProfileResolver riskProfileResolver,
    Func<string, string, decimal> crossRateProvider,
    PersistenceService persistence,
    OrderDispatcher orderDispatcher,
    PositionTracker positionTracker,
    ILogger<EngineWorker> logger,
    EngineRunContext runContext,
    DataFeedService? dataFeed = null,
    IProgress<BacktestProgressEvent>? progress = null)  // NEW — last optional param
```

Add field: `private readonly IProgress<BacktestProgressEvent>? _progress;`
In constructor body: `_progress = progress;`

**`BacktestProgressEvent` record** — define it in `TradingEngine.Host` namespace (same file as
EngineWorker, or a new file `src/TradingEngine.Host/BacktestProgressEvent.cs`):

```csharp
namespace TradingEngine.Host;

public sealed record BacktestProgressEvent(
    string RunId,
    string EventType,   // "BAR" | "SIGNAL" | "ORDER" | "FILL" | "TRADE"
    string Message,
    DateTime TimestampUtc);
```

**In `ProcessBarsAsync`**, after computing `intent`, add two report calls:

After the BAR_EVAL log line (newly changed to Debug), add:
```csharp
_progress?.Report(new BacktestProgressEvent(
    _runContext.RunId, "BAR",
    $"Bar {bar.OpenTimeUtc:yyyy-MM-dd HH:mm} | close={bar.Close:F5} | total={Interlocked.Read(ref _barCount)}",
    _clock.UtcNow));
```

After the `SIGNAL` log lines (lines ~242–245), add:
```csharp
_progress?.Report(new BacktestProgressEvent(
    _runContext.RunId, "SIGNAL",
    $"SIGNAL {strategy.Id} {intent.Direction} sl={intent.StopLoss.Value:F5} tp={intent.TakeProfit?.Value:F5} reason={intent.Reason}",
    _clock.UtcNow));
```

After `_persistence.SaveTradeAsync` (or in `TradePersistenceHandler`) — actually fire this from
`PositionTracker` via event bus. But the simplest approach: subscribe in the progress callback
to the `TradeClosed` event in EngineWorker. Add in `HandleAccountUpdate` or where `TradeClosed`
is published — actually the cleanest way is inside `ProcessBarsAsync` after the order tracking:

```csharp
_progress?.Report(new BacktestProgressEvent(
    _runContext.RunId, "ORDER",
    $"ORDER {strategy.Id} {intent.Direction} lots={orderCtx.Lots:F2} entry≈{bar.Close:F5}",
    _clock.UtcNow));
```

### A3 — Fix BarEvaluationHandler.DisposeAsync

Replace the existing `DisposeAsync`:

```csharp
public async ValueTask DisposeAsync()
{
    _channel.Writer.Complete();
    _cts.Cancel();
    try { await _flushTask; } catch { }

    // Flush any events remaining in the channel (previously silently dropped — DESIGN-07)
    var remaining = new List<BarEvaluated>(200);
    while (_channel.Reader.TryRead(out var evt))
        remaining.Add(evt);

    if (remaining.Count > 0)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            foreach (var evt in remaining)
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
            await db.SaveChangesAsync(CancellationToken.None);
            _logger.LogDebug("BarEvaluationHandler: flushed {Count} remaining events on dispose", remaining.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BarEvaluationHandler: failed to flush remaining events on dispose");
        }
    }

    _cts.Dispose();
}
```

---

## Phase B — Wire progress events through BacktestOrchestrator

**`BacktestOrchestrator.RunEngineReplayAsync`** (written in iter-12) — add `IProgress<BacktestProgressEvent>`
registration in the inner host:

```csharp
// Inside the inner host's ConfigureServices, after the existing registrations:
var progressCallback = new Progress<BacktestProgressEvent>(evt =>
{
    var prefix = evt.EventType switch
    {
        "SIGNAL" => "[SIGNAL]",
        "ORDER"  => "[ORDER] ",
        "TRADE"  => "[TRADE] ",
        _        => "[BAR]   ",
    };
    EnqueueLog(runId, logLines,
        $"[{evt.TimestampUtc:HH:mm:ss}] {prefix} {evt.Message}");
});
services.AddSingleton<IProgress<BacktestProgressEvent>>(_ => progressCallback);
```

`IProgress<T>` does not need to be registered per-run in Host/Program.cs (live mode never uses it).
It is only registered in the inner replay host.

---

## Phase C — Per-strategy breakdown

### C1 — New record + method in IBacktestQueryService

In `src/TradingEngine.Web/Services/IBacktestQueryService.cs`, add after `EquityPoint`:

```csharp
public sealed record StrategyPerformance(
    string StrategyId,
    int TotalBarsEvaluated,
    int SignalsFired,
    int TradesOpened,
    int Wins,
    int Losses,
    double WinRatePct,
    IReadOnlyList<(string Reason, int Count)> TopRejections);

// Add to interface:
Task<IReadOnlyList<StrategyPerformance>> GetStrategyBreakdownAsync(string runId, CancellationToken ct);
```

### C2 — Implement in BacktestQueryService

Add to `BacktestQueryService`:

```csharp
public async Task<IReadOnlyList<StrategyPerformance>> GetStrategyBreakdownAsync(
    string runId, CancellationToken ct)
{
    await using var scope = _scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

    // Bar evaluations aggregated per strategy + reason
    var evals = await db.BarEvaluations
        .Where(e => e.RunId == runId)
        .GroupBy(e => new { e.StrategyId, e.Reason, e.SignalFired })
        .Select(g => new
        {
            g.Key.StrategyId,
            g.Key.Reason,
            g.Key.SignalFired,
            Count = g.Count()
        })
        .ToListAsync(ct);

    // Trades per strategy
    var trades = await db.Trades
        .Where(t => t.RunId == runId)
        .GroupBy(t => t.StrategyId)
        .Select(g => new
        {
            StrategyId = g.Key,
            Total = g.Count(),
            Wins  = g.Count(t => t.NetPnLAmount > 0)
        })
        .ToListAsync(ct);

    var tradeIndex = trades.ToDictionary(t => t.StrategyId);
    var strategyIds = evals.Select(e => e.StrategyId).Distinct().ToList();

    return strategyIds.Select(sid =>
    {
        var stratEvals = evals.Where(e => e.StrategyId == sid).ToList();
        var total      = stratEvals.Sum(e => e.Count);
        var signals    = stratEvals.Where(e => e.SignalFired).Sum(e => e.Count);
        var noSignal   = stratEvals.Where(e => !e.SignalFired)
            .OrderByDescending(e => e.Count)
            .Take(5)
            .Select(e => (e.Reason, e.Count))
            .ToList();

        var t = tradeIndex.GetValueOrDefault(sid);
        var wins   = t?.Wins   ?? 0;
        var opened = t?.Total  ?? 0;
        var losses = opened - wins;
        var wr     = opened > 0 ? (double)wins / opened : 0d;

        return new StrategyPerformance(sid, total, signals, opened, wins, losses, wr, noSignal);
    }).ToList();
}
```

### C3 — Detail.cshtml.cs — load breakdown

In `src/TradingEngine.Web/Pages/Backtests/Detail.cshtml.cs`, add:

```csharp
public IReadOnlyList<StrategyPerformance> StrategyBreakdown { get; private set; } = [];
```

In `OnGet`, after setting `Run`:

```csharp
if (!IsActive && Run is not null)
{
    StrategyBreakdown = await _query.GetStrategyBreakdownAsync(runId, HttpContext.RequestAborted);
}
```

### C4 — Detail.cshtml — per-strategy table

After the existing summary `<table>`, add:

```html
@if (Model.StrategyBreakdown.Count > 0)
{
    <h3>Strategy Breakdown</h3>
    <table class="table">
        <thead>
            <tr><th>Strategy</th><th>Bars</th><th>Signals</th><th>Trades</th><th>W</th><th>L</th><th>Win%</th></tr>
        </thead>
        <tbody>
        @foreach (var s in Model.StrategyBreakdown)
        {
            <tr>
                <td>@s.StrategyId</td>
                <td>@s.TotalBarsEvaluated.ToString("N0")</td>
                <td>@s.SignalsFired</td>
                <td>@s.TradesOpened</td>
                <td>@s.Wins</td>
                <td>@s.Losses</td>
                <td>@s.WinRatePct.ToString("P0")</td>
            </tr>
            @if (s.TopRejections.Count > 0)
            {
                <tr>
                    <td colspan="7" style="padding-left:2rem;color:#888;font-size:0.85rem;">
                        Top rejections: @string.Join(" | ", s.TopRejections.Select(r => $"\"{r.Reason}\" ×{r.Count}"))
                    </td>
                </tr>
            }
        }
        </tbody>
    </table>
}
```

---

## Phase D — Progress page structured events

**`src/TradingEngine.Web/Pages/Backtests/Progress.cshtml`** — update the JS `evtSource.onmessage`:

Replace the existing handler with:

```javascript
let barCount = 0, signalCount = 0, tradeCount = 0;
const countersEl = document.getElementById('counters');

evtSource.onmessage = function(e) {
    const data = JSON.parse(e.data);

    if (data.eventType) {
        const el = document.createElement('div');
        el.style.cssText = eventStyle(data.eventType);
        el.textContent = data.message;
        logEl.appendChild(el);
        logEl.scrollTop = logEl.scrollHeight;

        if (data.eventType === 'BAR') barCount++;
        if (data.eventType === 'SIGNAL') signalCount++;
        if (data.eventType === 'TRADE') tradeCount++;
        if (countersEl) countersEl.textContent =
            `Bars: ${barCount.toLocaleString()} | Signals: ${signalCount} | Trades: ${tradeCount}`;
        return;
    }

    // Backwards compat: plain log line
    if (data.line) {
        logEl.textContent += data.line + '\n';
        logEl.scrollTop = logEl.scrollHeight;
    }

    if (data.done) {
        statusText.textContent = data.status;
        if (data.status === 'completed') resultDiv.style.display = 'block';
        if (data.status === 'failed' && data.error) {
            errorBox.style.display = 'block';
            errorText.textContent = data.error;
        }
        evtSource.close();
    }
};

function eventStyle(type) {
    const base = 'padding:1px 0;font-size:0.82rem;font-family:monospace;';
    if (type === 'SIGNAL') return base + 'color:#58a6ff;';
    if (type === 'ORDER')  return base + 'color:#3fb950;';
    if (type === 'TRADE')  return base + 'color:#ffa657;font-weight:bold;';
    return base + 'color:#8b949e;';  // BAR
}
```

Add a counters element to the HTML, just above `<h3>Log</h3>`:

```html
<div id="counters" style="font-size:0.9rem;color:#888;margin-bottom:0.5rem;">
    Bars: 0 | Signals: 0 | Trades: 0
</div>
```

Change `<pre id="log" ...>` to `<div id="log" style="background:#1e1e1e;...max-height:400px;overflow-y:auto;">`.
(Pre elements don't support child `<div>` nodes cleanly — switch to div.)

---

## Verification

```powershell
# Build
dotnet build --no-incremental

# Tests
dotnet test tests/TradingEngine.Tests.Unit --no-build
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "ReplayBacktest"

# Manual UI verification:
# 1. Run a replay backtest from /Backtests/Run
# 2. Progress page: bars stream in grey, at least one blue SIGNAL line visible
# 3. Live counter updates: "Bars: 1,240 | Signals: 3 | Trades: 1"
# 4. After completion, Detail page shows strategy breakdown table
# 5. Check DB:
#    SELECT StrategyId, COUNT(*) as Bars, SUM(SignalFired) as Signals
#    FROM BarEvaluations WHERE RunId = '<run-id>' GROUP BY StrategyId;
#    -- must have rows (DESIGN-07 fix verified)
```

---

## Forbidden list

- Do not change `TradePersistenceHandler.cs`
- Do not change `BacktestReplayAdapter.cs`
- Do not change the ctrader-cli path in `BacktestRunner.cs`
- Do not add EF migrations
- Do not change the `BarEvaluationEntity` schema

---

## Handover notes

_(Implementing agent fills this section)_

### Verification results

| Check | Result |
|-------|--------|
| `dotnet build --no-incremental` | |
| Unit tests | |
| `ReplayBacktest` simulation test | |
| BarEvaluations count > 0 after replay run | |
| Strategy breakdown table appears on Detail page | |
| Progress page shows colour-coded events | |
| Live counters update during run | |

### Issues closed

| Issue ID | Status |
|----------|--------|
| OBS-01 | |
| OBS-02 | |
| OBS-03 | |
| OBS-05 | |
| STD-03 | |
| DESIGN-07 | |
