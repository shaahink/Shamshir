# Iteration 14 — Blazor Server UI Rewrite

**Branch**: `iter/14-ui-blazor`
**Depends on**: Iteration 13 (needs correct data + strategy breakdown endpoints)
**Parallel with**: Iteration 15 (no file overlap — merge iter-15 first)

**Gate**: Do not start until iter-13 HANDOVER.md is filled and `BarEvaluations` count > 0 in DB.

---

## Read first

- `docs/agents/HOW-TO-WORK.md`
- `docs/agents/PARALLEL-AND-MODELS.md` — merge order for 14/15 parallel
- `src/TradingEngine.Web/Services/IBacktestQueryService.cs` — these are your data sources; do not change them
- `src/TradingEngine.Web/Services/IBacktestCommandService.cs` — run/cancel actions

---

## Why Blazor Server

- Domain types (`TradeResult`, `BarEvaluated`, `StrategyPerformance`) are C# records. Blazor uses
  them directly — no DTO serialisation.
- The existing `BacktestProgressStore` SSE channel maps directly to Blazor component `StateHasChanged()`.
- No separate build pipeline, no CORS, no token management.
- Single persistent connection (SignalR) per user. Fine for single-user localhost.
- TradingView Lightweight Charts renders OHLC + trade markers with a small JS interop wrapper.

---

## Scope: 5 sub-phases, each independently commitable

Do not implement multiple sub-phases in a single agent session. Each sub-phase is its own PR.

---

## Sub-phase 14A — Blazor project setup

**Agent time estimate**: half session

### Files to create / change

| File | Change |
|------|--------|
| `src/TradingEngine.Web/TradingEngine.Web.csproj` | Add Blazor package if missing |
| `src/TradingEngine.Web/Program.cs` | Add Blazor Server services |
| `src/TradingEngine.Web/App.razor` | New — root Blazor component |
| `src/TradingEngine.Web/_Imports.razor` | New — global using directives |
| `src/TradingEngine.Web/Pages/_Host.cshtml` | New — Blazor host page |
| `src/TradingEngine.Web/Shared/MainLayout.razor` | New — nav + layout shell |

### Program.cs changes

After `builder.Services.AddRazorPages();`:

```csharp
builder.Services.AddServerSideBlazor();
```

After `app.MapRazorPages();`:

```csharp
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
```

### App.razor

```razor
<Router AppAssembly="@typeof(App).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="@routeData" DefaultLayout="@typeof(MainLayout)" />
    </Found>
    <NotFound>
        <p>Page not found.</p>
    </NotFound>
</Router>
```

### _Imports.razor

```razor
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.JSInterop
@using TradingEngine.Web.Services
@using TradingEngine.Domain
```

### Pages/_Host.cshtml

```html
@page "/blazor"
@namespace TradingEngine.Web.Pages
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
<!DOCTYPE html>
<html>
<head>
    <title>Shamshir</title>
    <link rel="stylesheet" href="/css/site.css" />
    <base href="~/" />
</head>
<body>
    <component type="typeof(App)" render-mode="ServerPrerendered" />
    <script src="_framework/blazor.server.js"></script>
</body>
</html>
```

### Shared/MainLayout.razor

```razor
@inherits LayoutComponentBase

<nav style="background:#161b22;padding:0.75rem 1.5rem;display:flex;gap:1.5rem;margin-bottom:1rem;">
    <a href="/blazor/backtests" style="color:#58a6ff;text-decoration:none;">Backtests</a>
    <a href="/blazor/trades"    style="color:#58a6ff;text-decoration:none;">Trades</a>
</nav>

<main style="padding:1rem 1.5rem;">
    @Body
</main>
```

### Verification (14A)

```powershell
dotnet build --no-incremental
dotnet run --project src/TradingEngine.Web
# Navigate to /blazor — should load without error
# Existing /Backtests/Index Razor page must still work
```

---

## Sub-phase 14B — Backtest list + run pages (Blazor)

**Agent time estimate**: half session

All Blazor components live in `src/TradingEngine.Web/Pages/Blazor/`.

### BacktestList.razor — replaces Razor Pages /Backtests/Index

```
Route: /blazor/backtests
Inject: IBacktestQueryService, NavigationManager

OnInitializedAsync:
    runs = await _query.GetAllRunsAsync(ct)

Render:
    Table of BacktestRunView — RunId, Symbol, Period, TotalTrades, NetProfit, MaxDrawdownPct, Status
    Each row links to /blazor/backtests/{runId}
    "New Backtest" button links to /blazor/backtests/run
```

Concrete render (no pseudocode):

```razor
@page "/blazor/backtests"
@inject IBacktestQueryService Query
@inject NavigationManager Nav

<h1>Backtests</h1>
<a href="/blazor/backtests/run" class="btn-primary">New Backtest</a>

@if (_runs is null)
{
    <p>Loading...</p>
}
else if (_runs.Count == 0)
{
    <p>No backtest runs yet.</p>
}
else
{
    <table class="table">
        <thead>
            <tr><th>Run</th><th>Symbol</th><th>Period</th><th>Trades</th><th>Net P&L</th><th>Max DD</th><th>Status</th></tr>
        </thead>
        <tbody>
        @foreach (var r in _runs)
        {
            <tr style="cursor:pointer" @onclick="() => Nav.NavigateTo($\"/blazor/backtests/{r.RunId}\")">
                <td><code>@r.RunId</code></td>
                <td>@r.Symbol</td>
                <td>@r.Period</td>
                <td>@r.TotalTrades</td>
                <td style="color:@(r.NetProfit > 0 ? "#3fb950" : "#f85149")">@r.NetProfit.ToString("N2")</td>
                <td>@r.MaxDrawdownPct.ToString("P1")</td>
                <td>@r.Status</td>
            </tr>
        }
        </tbody>
    </table>
}

@code {
    private IReadOnlyList<BacktestRunView>? _runs;

    protected override async Task OnInitializedAsync()
    {
        _runs = await Query.GetAllRunsAsync(CancellationToken.None);
    }
}
```

### BacktestRun.razor — replaces Razor Pages /Backtests/Run

```
Route: /blazor/backtests/run
Inject: IBacktestCommandService, NavigationManager

Form fields: Symbol (EURUSD), Period (H1), Start date, End date, Balance
On submit: runId = await _command.StartAsync(cfg, ct)
On success: Nav.NavigateTo($"/blazor/backtests/{runId}/progress")
```

Mirror the existing `src/TradingEngine.Web/Pages/Backtests/Run.cshtml` form fields exactly.

### Verification (14B)

```powershell
dotnet build --no-incremental
dotnet run --project src/TradingEngine.Web
# /blazor/backtests shows existing runs from DB
# /blazor/backtests/run shows form, submitting redirects to progress page
# Existing Razor pages still work
```

---

## Sub-phase 14C — Live progress page (Blazor + SignalR)

**Agent time estimate**: full session

### BacktestProgress.razor

```
Route: /blazor/backtests/{RunId}/progress
Inject: BacktestProgressStore, IBacktestCommandService, NavigationManager
```

The component subscribes to the existing `BacktestProgressStore` channel directly (not via SSE —
Blazor Server has direct access to server objects). This avoids the SSE layer entirely for Blazor.

```csharp
// On init: read from the channel while it's open
// On each item: determine type (eventType field vs line field), append to log, StateHasChanged()
// On done: show result summary, enable "View Detail" button
```

Use a background `Task` started in `OnInitializedAsync` to drain the channel:

```razor
@page "/blazor/backtests/{RunId}/progress"
@inject BacktestProgressStore ProgressStore
@implements IAsyncDisposable

<h1>Backtest @RunId</h1>

<div style="font-size:0.9rem;color:#888;margin-bottom:0.5rem;">
    Bars: @_barCount | Signals: @_signalCount | Trades: @_tradeCount
</div>

<div id="log" style="background:#1e1e1e;padding:1rem;border-radius:4px;max-height:400px;overflow-y:auto;">
    @foreach (var line in _logLines)
    {
        <div style="@GetLineStyle(line.EventType)">@line.Text</div>
    }
</div>

@if (_done)
{
    <div style="margin-top:1rem;">
        <a href="/blazor/backtests/@RunId">View Full Report</a>
    </div>
}

@code {
    [Parameter] public string RunId { get; set; } = "";

    private sealed record LogLine(string EventType, string Text);
    private readonly List<LogLine> _logLines = [];
    private int _barCount, _signalCount, _tradeCount;
    private bool _done;
    private CancellationTokenSource _cts = new();

    protected override async Task OnInitializedAsync()
    {
        var reader = ProgressStore.GetReader(RunId);
        if (reader is null) { _done = true; return; }

        _ = Task.Run(async () =>
        {
            await foreach (var json in reader.ReadAllAsync(_cts.Token))
            {
                var data = System.Text.Json.JsonDocument.Parse(json).RootElement;
                var isDone = data.TryGetProperty("done", out var doneVal) && doneVal.GetBoolean();

                if (data.TryGetProperty("eventType", out var evtType))
                {
                    var type = evtType.GetString() ?? "BAR";
                    var msg  = data.GetProperty("message").GetString() ?? "";
                    _logLines.Add(new(type, msg));
                    if (type == "BAR")    _barCount++;
                    if (type == "SIGNAL") _signalCount++;
                    if (type == "TRADE")  _tradeCount++;
                }
                else if (data.TryGetProperty("line", out var lineVal))
                {
                    _logLines.Add(new("LOG", lineVal.GetString() ?? ""));
                }

                if (isDone) _done = true;
                await InvokeAsync(StateHasChanged);
            }
        }, _cts.Token);
    }

    private static string GetLineStyle(string type) => type switch
    {
        "SIGNAL" => "color:#58a6ff;font-size:0.82rem;",
        "ORDER"  => "color:#3fb950;font-size:0.82rem;",
        "TRADE"  => "color:#ffa657;font-weight:bold;font-size:0.82rem;",
        _        => "color:#8b949e;font-size:0.80rem;",
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
```

**`BacktestProgressStore`** needs a `GetReader` method that returns a `ChannelReader<string>` for
a given runId. Add it alongside the existing `GetWriter`:

```csharp
public ChannelReader<string>? GetReader(string runId)
    => _channels.TryGetValue(runId, out var ch) ? ch.Reader : null;
```

### Verification (14C)

```powershell
dotnet build --no-incremental
# Run a backtest from /blazor/backtests/run
# Progress page at /blazor/backtests/{runId}/progress:
# - BAR events appear in grey as they stream
# - Counters increment
# - At least one blue SIGNAL line
# - After completion, "View Full Report" link appears
```

---

## Sub-phase 14D — Equity curve + trade markers chart

**Agent time estimate**: 1–2 sessions

### Overview

Chart renders on the Detail page using TradingView Lightweight Charts via JS interop.
Three data series: OHLC candlesticks, equity line, trade markers.

### New data endpoint

In `src/TradingEngine.Web/Services/IBacktestQueryService.cs`, add:

```csharp
public sealed record OhlcBar(DateTime TimeUtc, decimal Open, decimal High, decimal Low, decimal Close);
public sealed record EquityPoint(DateTime TimestampUtc, decimal Equity);
public sealed record TradeMarker(DateTime EntryTimeUtc, DateTime ExitTimeUtc,
    string Direction, decimal EntryPrice, decimal ExitPrice,
    decimal NetPnL, string ExitReason);

// Add to interface:
Task<IReadOnlyList<OhlcBar>> GetBarsForRunAsync(string runId, CancellationToken ct);
Task<IReadOnlyList<EquityPoint>> GetEquityCurveAsync(string runId, decimal initialBalance, CancellationToken ct);
Task<IReadOnlyList<TradeMarker>> GetTradeMarkersAsync(string runId, CancellationToken ct);
```

### BacktestQueryService — implement new methods

`GetBarsForRunAsync`: join `BarEvaluations` with `Bars` table on `Symbol + Timeframe + BarOpenTimeUtc`,
filter by RunId, return distinct bars in time order.

```csharp
public async Task<IReadOnlyList<OhlcBar>> GetBarsForRunAsync(string runId, CancellationToken ct)
{
    await using var scope = _scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

    // Get symbol/timeframe from the run's bar evaluations
    var meta = await db.BarEvaluations
        .Where(e => e.RunId == runId)
        .Select(e => new { e.Symbol, e.Timeframe })
        .FirstOrDefaultAsync(ct);
    if (meta is null) return [];

    return await db.Bars
        .Where(b => b.Symbol == meta.Symbol && b.Timeframe == meta.Timeframe)
        .OrderBy(b => b.OpenTimeUtc)
        .Select(b => new OhlcBar(b.OpenTimeUtc, b.Open, b.High, b.Low, b.Close))
        .ToListAsync(ct);
}
```

`GetEquityCurveAsync`: compute running equity from trades ordered by ClosedAtUtc:

```csharp
public async Task<IReadOnlyList<EquityPoint>> GetEquityCurveAsync(
    string runId, decimal initialBalance, CancellationToken ct)
{
    await using var scope = _scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

    var trades = await db.Trades
        .Where(t => t.RunId == runId)
        .OrderBy(t => t.ClosedAtUtc)
        .Select(t => new { t.ClosedAtUtc, t.NetPnLAmount })
        .ToListAsync(ct);

    var points = new List<EquityPoint>(trades.Count + 1);
    var equity = initialBalance;
    foreach (var t in trades)
    {
        equity += t.NetPnLAmount;
        points.Add(new EquityPoint(t.ClosedAtUtc, equity));
    }
    return points;
}
```

`GetTradeMarkersAsync`: return all trades with entry/exit time and price:

```csharp
public async Task<IReadOnlyList<TradeMarker>> GetTradeMarkersAsync(
    string runId, CancellationToken ct)
{
    await using var scope = _scopeFactory.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

    return await db.Trades
        .Where(t => t.RunId == runId)
        .OrderBy(t => t.OpenedAtUtc)
        .Select(t => new TradeMarker(
            t.OpenedAtUtc, t.ClosedAtUtc,
            t.Direction, t.EntryPrice, t.ExitPrice,
            t.NetPnLAmount, t.ExitReason))
        .ToListAsync(ct);
}
```

### JS interop wrapper

Create `src/TradingEngine.Web/wwwroot/js/chart-interop.js`:

```javascript
window.chartInterop = {
    _chart: null,
    _candleSeries: null,
    _equitySeries: null,

    init(containerId) {
        const el = document.getElementById(containerId);
        if (!el) return;
        this._chart = LightweightCharts.createChart(el, {
            width: el.clientWidth, height: 400,
            layout: { background: { color: '#161b22' }, textColor: '#c9d1d9' },
            grid: { vertLines: { color: '#21262d' }, horzLines: { color: '#21262d' } },
            crosshair: { mode: LightweightCharts.CrosshairMode.Normal },
            rightPriceScale: { borderColor: '#30363d' },
            timeScale: { borderColor: '#30363d', timeVisible: true },
        });
        this._candleSeries = this._chart.addCandlestickSeries({
            upColor: '#3fb950', downColor: '#f85149',
            wickUpColor: '#3fb950', wickDownColor: '#f85149', borderVisible: false,
        });
        this._equitySeries = this._chart.addLineSeries({
            color: '#58a6ff', lineWidth: 1, priceScaleId: 'equity',
        });
        this._chart.priceScale('equity').applyOptions({ scaleMargins: { top: 0.7, bottom: 0 } });
    },

    setCandles(bars) {
        // bars: [{time: unix_seconds, open, high, low, close}]
        this._candleSeries.setData(bars);
    },

    setEquity(points) {
        // points: [{time: unix_seconds, value: equity}]
        this._equitySeries.setData(points);
    },

    addMarkers(markers) {
        // markers: [{time, position:'aboveBar'|'belowBar', color, shape:'arrowUp'|'arrowDown', text}]
        this._candleSeries.setMarkers(markers);
    },
};
```

Add `<script src="https://unpkg.com/lightweight-charts/dist/lightweight-charts.standalone.production.js"></script>`
and `<script src="/js/chart-interop.js"></script>` to `_Host.cshtml`.

### BacktestDetail.razor (new Blazor component)

Route: `/blazor/backtests/{RunId}`

```razor
@page "/blazor/backtests/{RunId}"
@inject IBacktestQueryService Query
@inject IJSRuntime JS

<div id="chart-container" style="width:100%;"></div>

@code {
    [Parameter] public string RunId { get; set; } = "";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        await JS.InvokeVoidAsync("chartInterop.init", "chart-container");

        var run = await Query.GetRunAsync(RunId, CancellationToken.None);
        if (run is null) return;

        var bars    = await Query.GetBarsForRunAsync(RunId, CancellationToken.None);
        var equity  = await Query.GetEquityCurveAsync(RunId, run.InitialBalance, CancellationToken.None);
        var markers = await Query.GetTradeMarkersAsync(RunId, CancellationToken.None);

        // Convert to JS format (unix seconds)
        var candleData = bars.Select(b => new {
            time  = ((DateTimeOffset)b.TimeUtc).ToUnixTimeSeconds(),
            open  = (double)b.Open, high  = (double)b.High,
            low   = (double)b.Low,  close = (double)b.Close
        });
        var equityData = equity.Select(e => new {
            time  = ((DateTimeOffset)e.TimestampUtc).ToUnixTimeSeconds(),
            value = (double)e.Equity
        });
        var markerData = markers.Select(m => new {
            time     = ((DateTimeOffset)m.EntryTimeUtc).ToUnixTimeSeconds(),
            position = m.Direction == "Long" ? "belowBar" : "aboveBar",
            color    = m.Direction == "Long" ? "#3fb950" : "#f85149",
            shape    = m.Direction == "Long" ? "arrowUp" : "arrowDown",
            text     = $"{m.ExitReason} {m.NetPnL:+#.##;-#.##;0}"
        });

        await JS.InvokeVoidAsync("chartInterop.setCandles", candleData);
        await JS.InvokeVoidAsync("chartInterop.setEquity", equityData);
        await JS.InvokeVoidAsync("chartInterop.addMarkers", markerData);
    }
}
```

Add the run summary panel and strategy breakdown table above the chart (same data as Detail.cshtml).

### Verification (14D)

```powershell
dotnet build --no-incremental
dotnet run --project src/TradingEngine.Web
# Navigate to /blazor/backtests/{runId} for a completed run with trades
# Chart renders — OHLC candles visible
# Equity line visible below candles
# Trade entry markers (green arrows up, red arrows down) on entry bars
# No JS console errors
```

---

## Sub-phase 14E — Full detail page assembly

**Agent time estimate**: half session

Assemble everything onto `BacktestDetail.razor`:

1. **Run metadata panel** (top): Symbol, Period, Start–End, Balance, AlgoHash, Status
2. **Equity chart** (14D — already there)
3. **Trade list table**: Entry time, Exit time, Direction, Lots, Entry, Exit, PnL, R-multiple,
   Exit reason, Strategy — sortable by PnL
4. **Per-strategy table** from iter-13 `StrategyPerformance`
5. **"Cancel" button** (only when status = "running") — calls `IBacktestCommandService.Cancel`

The Detail page replaces both `Detail.cshtml` and `Progress.cshtml`. During an active run,
re-use `BacktestProgress.razor` components (or embed the live log directly).

### Verification (14E)

```powershell
dotnet build --no-incremental
# Manual: full detail page renders with all 5 sections for a completed run
# Active run: cancel button visible, clicking it sets status to "cancelled"
# Existing Razor pages still work (transition is additive, not replace-and-delete)
```

---

## Global forbidden list for all iter-14 sub-phases

- Do not delete existing Razor Pages — add Blazor routes alongside
- Do not change any service, repository, or domain code
- Do not change channel configurations
- Do not put business logic in Blazor components — use the query/command services
- Do not use `IJSRuntime` for anything except the chart
- Do not add EF migrations

---

## Handover notes (per sub-phase)

### 14A

_(agent fills)_

### 14B

_(agent fills)_

### 14C

_(agent fills)_

### 14D

_(agent fills)_

### 14E

_(agent fills)_
