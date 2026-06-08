# Iteration 12 — Wire Replay to UI + Correct Metrics

**Branch**: `iter/12-replay-ui-wire`
**Fixes**: DESIGN-05, BUG-04, OBS-04 from `docs/OPEN-ISSUES.md`
**Depends on**: Iteration 11 — `ReplayBacktest_FullPipeline_ProducesBarEvaluations` must pass
**Blocks**: Iteration 13

**Gate**: `dotnet test tests/TradingEngine.Tests.Simulation --filter "ReplayBacktest"` green before starting.

---

## Read first

- `docs/agents/HOW-TO-WORK.md`
- `docs/reference/BACKTEST-ARCHITECTURE.md`
- `docs/iterations/iter-11/HANDOVER.md` — **critical**: shutdown pattern, constructor mismatches
- `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` — current implementation
- `src/TradingEngine.Host/Program.cs` — DI registrations to replicate in inner host
- `tests/TradingEngine.Tests.Simulation/Harness/ReplayTestHarness.cs` — working inner-host pattern

### Agent notes (added post-iter-11)

**1. Constructor signatures** — The `RunEngineReplayAsync` inner host must match real types,
not the plan's inline examples. Key types to verify before writing DI:

| Type | Reference file |
|------|---------------|
| `SymbolInfo` (12 params) | `src/TradingEngine.Domain/SymbolInfo/SymbolInfo.cs` |
| `RiskProfile` (18 params) | `src/TradingEngine.Domain/RiskAndEquity/RiskProfile.cs` |
| `RiskProfileResolver` | Takes `IReadOnlyList<RiskProfile>`, not dictionary |
| `ISymbolInfoRegistry` | Copy from `Host/Program.cs` lines 73-97 |
| `ConfigLoader` | Duplicate instantiation at lines 318 and 337 — one instance suffices |

**2. Shutdown pattern** — `WaitForShutdownAsync` alone **hangs** because `_executionChannel`
is never completed during normal operation (see iter-11 HANDOVER item 1). `ProcessExecutionEventsAsync`
blocks on `ReadAllAsync` forever, so `EngineWorker.ExecuteAsync` never returns, and the host
never self-stops. The working pattern from `ReplayTestHarness.RunAsync`:

```csharp
await innerHost.StartAsync(cts.Token);
var adapter = innerHost.Services.GetRequiredService<IBrokerAdapter>();
await adapter.BarStream.Completion;       // all bars consumed
await Task.Delay(5_000, cts.Token);       // flush grace for BarEvaluationHandler (3s cycle)
await innerHost.StopAsync(CancellationToken.None);
innerHost.Dispose();
```

Replace the plan's lines 375-382 (the `StartAsync` → `WaitForShutdownAsync` → `StopAsync` block)
with this pattern.

---

## What is wrong

**OBS-04 — UI always uses ctrader-cli, never the replay adapter**
`BacktestOrchestrator.RunAsync` unconditionally calls `BacktestRunner.RunAsync` (ctrader-cli subprocess).
The replay adapter built in iter-11 is never invoked by the UI. Result: the UI requires cTrader
credentials to run any backtest at all.

**DESIGN-05 — Failed runs leave no DB record**
`SaveAsync` is inside `if (result.Success)`. Any run that errors leaves `BacktestRuns` empty for
that runId. `GetAllRunsAsync` returns an empty list even for runs the user actually ran.

**BUG-04 — Max drawdown is fabricated**
```csharp
var maxDd = Math.Abs(trades.Min(t => t.NetPnLAmount)) / 100_000m;
```
This is: worst single trade loss / hardcoded 100,000. It's not drawdown.
Correct drawdown: peak-to-trough of the cumulative equity curve built from trades.

---

## Files to change

| File | Change | Phase |
|------|--------|-------|
| `src/TradingEngine.Web/TradingEngine.Web.csproj` | Add Host project reference | A |
| `src/TradingEngine.Domain/Interfaces/IBacktestRunRepository.cs` | Add `UpdateAsync` | A |
| `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteBacktestRunRepository.cs` | Implement `UpdateAsync` | A |
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Branch on config; add `RunEngineReplayAsync`; fix save | B |
| `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` | Fix `GetTradeStatsAsync` max drawdown | C |
| `src/TradingEngine.Web/Program.cs` | Register `IBarRepository` | A |

**Do NOT touch**: `BacktestRunner.cs`, `BacktestConfig.cs`, `BacktestResult.cs`, `EngineWorker.cs`,
any handler, any migration, any test file.

---

## Phase A — Repository `UpdateAsync` + project reference

### A1 — Web project reference

In `src/TradingEngine.Web/TradingEngine.Web.csproj`, inside the `<ItemGroup>` with project references, add:

```xml
<ProjectReference Include="..\TradingEngine.Host\TradingEngine.Host.csproj" />
```

Also add `TradingEngine.Services` if not already referenced (needed for `PositionTracker`):

```xml
<ProjectReference Include="..\TradingEngine.Services\TradingEngine.Services.csproj" />
<ProjectReference Include="..\TradingEngine.Strategies\TradingEngine.Strategies.csproj" />
<ProjectReference Include="..\TradingEngine.Risk\TradingEngine.Risk.csproj" />
```

Verify with `dotnet build` — 0 errors before proceeding.

### A2 — IBacktestRunRepository

In `src/TradingEngine.Domain/Interfaces/IBacktestRunRepository.cs`, add one method to the interface:

```csharp
Task UpdateAsync(BacktestRunSummary run, CancellationToken ct);
```

### A3 — SqliteBacktestRunRepository

In `src/TradingEngine.Infrastructure/Persistence/Repositories/SqliteBacktestRunRepository.cs`,
add after `SaveAsync`:

```csharp
public async Task UpdateAsync(BacktestRunSummary run, CancellationToken ct)
{
    var entity = await db.BacktestRuns.FindAsync([run.RunId], ct);
    if (entity is null) return;
    entity.CompletedAtUtc = run.CompletedAtUtc;
    entity.NetProfit = run.NetProfit;
    entity.MaxDrawdownPct = run.MaxDrawdownPct;
    entity.TotalTrades = run.TotalTrades;
    entity.WinningTrades = run.WinningTrades;
    entity.WinRatePct = run.WinRatePct;
    entity.ExitCode = run.ExitCode;
    entity.ErrorMessage = run.ErrorMessage;
    await db.SaveChangesAsync(ct);
}
```

### A4 — Register IBarRepository in Web/Program.cs

After the existing `AddScoped<IBacktestRunRepository>` line, add:

```csharp
builder.Services.AddScoped<IBarRepository, SqliteBarRepository>();
```

---

## Phase B — Wire replay into BacktestOrchestrator

This is the largest change. Replace `RunAsync` entirely and add `RunEngineReplayAsync`.

### B1 — `BacktestOrchestrator.cs`: new fields and helpers

At the top of the class, add:

```csharp
private readonly IConfiguration _configuration;
```

Update the constructor to accept it:

```csharp
public BacktestOrchestrator(
    IServiceScopeFactory scopeFactory,
    BacktestProgressStore progressStore,
    IConfiguration configuration,
    ILogger<BacktestOrchestrator> logger)
{
    _scopeFactory = scopeFactory;
    _progressStore = progressStore;
    _configuration = configuration;
    _logger = logger;
}
```

Add a helper to parse timeframe from the config period string:

```csharp
private static Timeframe ParseTimeframe(string period) => period.ToUpperInvariant() switch
{
    "M1"  => Timeframe.M1,
    "M5"  => Timeframe.M5,
    "M15" => Timeframe.M15,
    "M30" => Timeframe.M30,
    "H1"  => Timeframe.H1,
    "H4"  => Timeframe.H4,
    "D1"  => Timeframe.D1,
    _     => Timeframe.H1,
};
```

### B2 — New `RunAsync` — branches on config flag

Replace the existing `RunAsync` body with:

```csharp
private async Task RunAsync(string runId, BacktestConfig cfg)
{
    var state = _runs[runId];
    var startedAt = state.StartedAt;

    // Write "in-progress" row immediately so the UI can show it
    await WriteStartRecordAsync(runId, cfg, startedAt);

    try
    {
        state.Status = "running";
        EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Starting backtest {runId}...");

        BacktestResult result;

        var useCtader = _configuration.GetValue<bool>("CTrader:UseForBacktest");
        if (useCtader)
        {
            EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Running via ctrader-cli...");
            using var scope = _scopeFactory.CreateScope();
            var runnerLogger = scope.ServiceProvider.GetRequiredService<ILogger<BacktestRunner>>();
            var runner = new BacktestRunner(_configuration, runnerLogger);
            result = await runner.RunAsync(cfg);
        }
        else
        {
            EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Running engine replay...");
            result = await RunEngineReplayAsync(runId, cfg, state.LogLines);
        }

        var tradeStats = await GetTradeStatsAsync(runId, cfg.Balance);

        result = result with
        {
            NetProfit      = tradeStats.NetProfit,
            MaxDrawdownPct = tradeStats.MaxDrawdownPct,
            TotalTrades    = tradeStats.TotalTrades,
            WinningTrades  = tradeStats.WinningTrades,
            WinRatePct     = tradeStats.WinRatePct,
        };

        state.Result = result;
        state.Status = result.Success ? "completed" : "failed";
        state.Error = result.ErrorMessage;

        EnqueueLog(runId, state.LogLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Done. Trades={result.TotalTrades} PnL={result.NetProfit:N2} DD={result.MaxDrawdownPct:P1}");

        await WriteEndRecordAsync(runId, cfg, startedAt, result, tradeStats);
    }
    catch (Exception ex)
    {
        state.Status = "failed";
        state.Error = ex.Message;
        EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Error: {ex.Message}");
        _logger.LogError(ex, "Backtest {RunId} failed", runId);

        // Still write the failure record
        await WriteEndRecordAsync(runId, cfg, startedAt,
            new BacktestResult { RunId = runId, ExitCode = 1, ErrorMessage = ex.Message },
            new(0, 0, 0, 0, 0));
    }
    finally
    {
        var doneJson = System.Text.Json.JsonSerializer.Serialize(
            new { done = true, status = state.Status, error = state.Error });
        _progressStore.GetWriter(runId).TryWrite(doneJson);
        _progressStore.Complete(runId);
    }
}
```

### B3 — `WriteStartRecordAsync` and `WriteEndRecordAsync` helpers

```csharp
private async Task WriteStartRecordAsync(string runId, BacktestConfig cfg, DateTime startedAt)
{
    try
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
        var summary = new BacktestRunSummary(
            runId, startedAt, DateTime.MinValue,
            cfg.Symbol, cfg.Period, cfg.Start, cfg.End,
            cfg.Balance, "", "{}",
            0, 0, 0, 0, 0, -1, null);   // ExitCode -1 = in-progress
        await repo.SaveAsync(summary, CancellationToken.None);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to write start record for {RunId}", runId);
    }
}

private async Task WriteEndRecordAsync(
    string runId, BacktestConfig cfg, DateTime startedAt,
    BacktestResult result, TradeStats stats)
{
    try
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
        var summary = new BacktestRunSummary(
            runId, startedAt, DateTime.UtcNow,
            cfg.Symbol, cfg.Period, cfg.Start, cfg.End,
            cfg.Balance, result.AlgoHash, "{}",
            stats.NetProfit, stats.MaxDrawdownPct,
            stats.TotalTrades, stats.WinningTrades, stats.WinRatePct,
            result.ExitCode, result.ErrorMessage);
        await repo.UpdateAsync(summary, CancellationToken.None);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to write end record for {RunId}", runId);
    }
}
```

### B4 — `RunEngineReplayAsync`

```csharp
private async Task<BacktestResult> RunEngineReplayAsync(
    string runId, BacktestConfig cfg, ConcurrentQueue<string> logLines)
{
    var symbol    = Symbol.Parse(cfg.Symbol);
    var timeframe = ParseTimeframe(cfg.Period);
    var from      = cfg.Start;
    var to        = cfg.End;

    var dbPath = _configuration.GetValue<string>("Persistence:DbPath")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "data", "trading.db"));

    using var scope = _scopeFactory.CreateScope();
    var barRepo = scope.ServiceProvider.GetRequiredService<IBarRepository>();

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));

    var innerHost = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
        .ConfigureLogging(l => l.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning))
        .ConfigureServices((ctx, services) =>
        {
            services.AddSingleton(new EngineRunContext(runId));
            services.AddSingleton<IBarRepository>(_ => barRepo);
            services.AddSingleton<IBrokerAdapter>(sp =>
                new BacktestReplayAdapter(barRepo, symbol, timeframe, from, to,
                    cfg.Balance, sp.GetRequiredService<ILogger<BacktestReplayAdapter>>()));

            // Symbol registry — read from config/symbols/defaults.json (same as Host/Program.cs)
            services.AddSingleton<ISymbolInfoRegistry>(sp =>
            {
                var reg = new SymbolInfoRegistry();
                var configLoader = new ConfigLoader(FindSolutionRoot());
                var loaded = configLoader.Load();
                // ConfigLoader already parses symbols; access them via loaded.SymbolInfos or
                // re-read the json file at the same path used in Host/Program.cs
                return reg; // agent: fill this using the pattern from Host/Program.cs lines 73-97
            });

            services.AddSingleton<Func<string, string, decimal>>(_ => (from, to) =>
            {
                if (from == "JPY" && to == "USD") return 1m / 149.50m;
                if (from == "GBP" && to == "USD") return 1.2650m;
                return 1m;
            });

            services.AddSingleton<DrawdownTracker>();
            services.AddSingleton<RiskManager>();
            services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());

            // Load config for risk profiles and strategies
            var configLoader2 = new ConfigLoader(FindSolutionRoot());
            var loadedConfig = configLoader2.Load();
            services.AddSingleton(loadedConfig);
            services.AddSingleton<IRiskProfileResolver>(sp =>
                new RiskProfileResolver(sp.GetRequiredService<LoadedConfig>().RiskProfiles));

            services.AddSingleton<IEngineClock, BrokerClock>();

            services.AddDbContext<TradingDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
            services.AddScoped<ITradeRepository, SqliteTradeRepository>();
            services.AddScoped<IEquityRepository, SqliteEquityRepository>();
            services.AddSingleton<PersistenceService>();

            services.AddSingleton<IPositionManager, PositionManager>();
            services.AddSingleton<IEventBus, TypedEventBus>();
            services.AddSingleton<EquityPersistenceHandler>();
            services.AddSingleton<TradePersistenceHandler>();
            services.AddSingleton<BarEvaluationHandler>();
            services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
            services.AddSingleton<OrderDispatcher>();
            services.AddSingleton<PositionTracker>();

            // Strategies
            var registry = new StrategyRegistry();
            services.AddSingleton(registry);
            services.AddSingleton<IEnumerable<IStrategy>>(sp =>
            {
                var reg = sp.GetRequiredService<StrategyRegistry>();
                var loaded = sp.GetRequiredService<LoadedConfig>();
                var activeIds = loaded.StrategyConfigs.Select(c => c.StrategyId).ToArray();
                return reg.CreateStrategies(activeIds, loaded, sp);
            });

            services.AddSingleton<EngineWorker>();
            services.AddHostedService<EngineWorker>(sp => sp.GetRequiredService<EngineWorker>());
        })
        .Build();

    await innerHost.StartAsync(cts.Token);
    EnqueueLog(runId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] Engine started. Replaying bars...");

    await innerHost.WaitForShutdownAsync(cts.Token);

    EnqueueLog(runId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] Engine replay complete.");
    await innerHost.StopAsync(CancellationToken.None);
    innerHost.Dispose();

    return new BacktestResult { RunId = runId, ExitCode = 0, AlgoHash = "" };
}

private static string FindSolutionRoot()
    => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", ".."));
```

**Note to implementing agent**: Fill in the `ISymbolInfoRegistry` registration by copying the exact
block from `Host/Program.cs` lines 73–97. The pattern is the same — load `config/symbols/defaults.json`
relative to the solution root.

---

## Phase C — Fix `GetTradeStatsAsync` max drawdown

Replace the entire `GetTradeStatsAsync` method. The signature gains `initialBalance`:

```csharp
private async Task<TradeStats> GetTradeStatsAsync(string runId, decimal initialBalance)
{
    try
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var trades = await db.Trades
            .Where(t => t.RunId == runId)
            .OrderBy(t => t.ClosedAtUtc)
            .ToListAsync();

        if (trades.Count == 0) return new(0, 0, 0, 0, 0);

        var netPnL = trades.Sum(t => t.NetPnLAmount);
        var wins   = trades.Count(t => t.NetPnLAmount > 0);
        var winRate = (double)wins / trades.Count;

        // Correct peak-to-trough drawdown using running equity curve
        var equity   = initialBalance;
        var peak     = initialBalance;
        var maxDd    = 0m;
        foreach (var t in trades)
        {
            equity += t.NetPnLAmount;
            if (equity > peak) peak = equity;
            if (peak > 0)
            {
                var dd = (peak - equity) / peak;
                if (dd > maxDd) maxDd = dd;
            }
        }

        return new(netPnL, maxDd, trades.Count, wins, winRate);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to query trade stats for {RunId}", runId);
        return new(0, 0, 0, 0, 0);
    }
}
```

Update the two call sites in `RunAsync` that previously called `GetTradeStatsAsync(runId)` to
`GetTradeStatsAsync(runId, cfg.Balance)`.

---

## Verification

```powershell
# 1. Build
dotnet build --no-incremental

# 2. Existing tests must not regress
dotnet test tests/TradingEngine.Tests.Unit --no-build
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "ReplayBacktest"

# 3. Manual UI smoke test
# Start: dotnet run --project src/TradingEngine.Web
# Navigate to /Backtests/Run
# Submit a backtest (ensure CTrader:UseForBacktest is NOT set or is false in appsettings)
# Navigate to /Backtests/Index — should see a row (even for failed runs)
# Navigate to the Detail page — TotalTrades > 0

# 4. DB verification (run after a completed backtest)
# sqlite3 data/trading.db
# SELECT RunId, TotalTrades, ExitCode, MaxDrawdownPct FROM BacktestRuns ORDER BY StartedAtUtc DESC LIMIT 5;
# -- ExitCode -1 row (in-progress) should not appear; should be 0 (success) or 1 (failure)
# -- MaxDrawdownPct should be a real value (e.g., 0.008) not 0.00001
# SELECT COUNT(*) FROM TradeResults WHERE RunId = '<your-run-id>';
```

---

## If the inner host fails to start

The most likely cause is a missing DI registration. The error message will name the missing service.
Check Host/Program.cs for how that service is registered and add the equivalent to `RunEngineReplayAsync`.

If `ISymbolInfoRegistry` throws (no symbols loaded), verify the path to `config/symbols/defaults.json`
relative to `AppContext.BaseDirectory`. In tests it resolves from the test output directory, not the
solution root — adjust `FindSolutionRoot()` if needed.

If strategies produce 0 results, check that `StrategyRegistry.CreateStrategies` receives the correct
`activeIds` — they should match the IDs in the config JSON.

---

## Forbidden list

- Do not change `BacktestRunner.cs` — the ctrader-cli path stays intact
- Do not change `BacktestConfig.cs` or `BacktestResult.cs`
- Do not change `EngineWorker.cs`
- Do not change any handler
- Do not add EF migrations
- Do not remove the `if (useCtader)` branch

---

## Handover notes

_(Implementing agent fills this section)_

### Verification results

| Check | Result |
|-------|--------|
| `dotnet build --no-incremental` | |
| Unit tests | |
| `ReplayBacktest` simulation test | |
| BacktestRuns row written for failed run | |
| BacktestRuns row has correct ExitCode after completion | |
| MaxDrawdownPct is realistic (not near-zero) | |
| TotalTrades > 0 after a replay run | |

### Issues closed

| Issue ID | Status |
|----------|--------|
| OBS-04 | |
| DESIGN-05 | |
| BUG-04 | |

### Anything that deviated from the plan

_(Note DI registration differences, namespace issues, etc.)_
