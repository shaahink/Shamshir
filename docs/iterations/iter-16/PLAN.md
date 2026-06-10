# Iteration 16 — cTrader In-Process Engine + Remaining Issues

**Branch**: `iter/16-ctrader-inproc`
**Base**: `dev` (after merging `iter/15-ctrader-pipeline`)
**Blocks**: Nothing — this is the final cTrader pipeline iteration

---

## Read first

- `docs/iterations/iter-15-ctrader-pipeline/HANDOVER.md` — what iter-15 delivered
- `docs/OPEN-ISSUES.md` — canonical issues list (some already marked fixed)
- `src/TradingEngine.CTraderRunner/BacktestRunner.cs` — current subprocess-based cTrader launch
- `src/TradingEngine.Web/Services/BacktestOrchestrator.cs` — `RunEngineReplayAsync` (inner host pattern to replicate)
- `src/TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs` — NetMQ adapter
- `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` — cBot command handler

---

## Phase A — Quick fixes (blockers for UI/dev workflow)

### A1 — Fix NuGet version conflict in integration tests

**File**: `tests/TradingEngine.Tests.Integration/TradingEngine.Tests.Integration.csproj`

`Microsoft.EntityFrameworkCore.InMemory` is pinned to `10.0.8` but EF Core in
`TradingEngine.Infrastructure.csproj` is `10.0.9`. This causes NU1605 downgrade
warning treated as error — `dotnet build` fails.

**Fix**: Bump to `10.0.9`:
```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.0.9" />
```

Also bump `Microsoft.AspNetCore.Mvc.Testing` from `10.0.8` to `10.0.9` for consistency.

### A2 — Default UI to replay mode

**File**: `src/TradingEngine.Web/appsettings.Development.json`

Revert `UseForBacktest` from `"true"` to `"false"`. This makes the UI use the
reliable replay path by default. cTrader path should be explicit opt-in.

```json
"UseForBacktest": "false"
```

### A3 — Relax Bars-assertion for integration tests

**File**: `src/TradingEngine.Web/Program.cs`

The `ctx.Bars.Any()` assertion at line ~39 crashes the Web app when the DB is empty.
Integration tests (`WebApplicationFactory`) can't start. Change from a crash to a
warning so the integration test web host can start without seeded bars.

```csharp
// BEFORE:
throw new InvalidOperationException("Bars table is empty...");

// AFTER:
_logger.LogWarning("Bars table is empty. Run scripts/seed-bars.ps1...");
```

Need to add `ILogger<Program>` or use a static log. Simplest: use `Console.WriteLine`
or add a simple static logger at the top of Program.cs.

### A4 — Verify with seeded bars

After A1-A3, seed bars and verify integration tests pass:
```powershell
powershell -ExecutionPolicy Bypass -File scripts\seed-bars.ps1
dotnet build --no-incremental   # 0 errors
dotnet test tests/TradingEngine.Tests.Unit --no-build        # 87/87
dotnet test tests/TradingEngine.Tests.Integration --no-build  # 15/15
dotnet test tests/TradingEngine.Tests.Simulation --no-build   # 11/11
```

---

## Phase B — In-process cTrader engine (main deliverable)

### Goal

Replace the engine subprocess with an in-process `IHost` using `NetMQBrokerAdapter`.
Same pattern as `RunEngineReplayAsync` but with NetMQ adapter instead of replay.

### Why

| Before (subprocess) | After (in-process) |
|---------------------|-------------------|
| Engine started via `dotnet run --project Host` | Engine runs inside Web app process |
| `WaitForEngineReadyAsync` TCP probe for readiness | No wait — engine starts synchronously |
| No progress events in UI | SSE progress events work |
| Orphan process risk | No orphan processes |
| Separate DB risk (Persistence__DbPath) | Same process, same DB |
| 15-30s overhead per backtest | Instant startup |

### B1 — Create `RunEngineNetMqAsync` in `BacktestOrchestrator`

**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs`

New private method, modeled after `RunEngineReplayAsync` but using `NetMQBrokerAdapter`:

```csharp
private async Task<BacktestResult> RunEngineNetMqAsync(
    string runId, BacktestConfig cfg, ConcurrentQueue<string> logLines)
{
    var symbol    = Symbol.Parse(cfg.Symbol);
    var timeframe = ParseTimeframe(cfg.Period);
    var from      = cfg.Start;
    var to        = cfg.End;

    var dataPort  = int.TryParse(_configuration["Engine:Broker:NetMQ:DataPort"], out var dp) ? dp : 15555;
    var commandPort = int.TryParse(_configuration["Engine:Broker:NetMQ:CommandPort"], out var cp) ? cp : 15556;
    var dbPath = _configuration.GetValue<string>("Persistence:DbPath")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "data", "trading.db"));

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

    var innerHost = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
        .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning))
        .ConfigureServices((ctx, services) =>
        {
            services.AddSingleton(new EngineRunContext(runId));

            // NetMQ broker adapter (engine subscribes to cBot's PUB, binds ROUTER for commands)
            services.AddSingleton<IBrokerAdapter>(sp =>
                new NetMQBrokerAdapter(
                    $"tcp://127.0.0.1:{dataPort}",
                    $"tcp://*:{commandPort}",
                    sp.GetRequiredService<ILogger<NetMQBrokerAdapter>>()));

            // --- Same DI as RunEngineReplayAsync from here down ---
            var symbolInfo = new SymbolInfo(symbol, SymbolCategory.Forex, "EUR", "USD",
                0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);
            var symbolRegistry = new SymbolInfoRegistry();
            symbolRegistry.Register(symbolInfo);
            services.AddSingleton<ISymbolInfoRegistry>(_ => symbolRegistry);

            services.AddSingleton<Func<string, string, decimal>>(_ => (fromCur, toCur) =>
            {
                if (fromCur == "JPY" && toCur == "USD") return 1m / 149.50m;
                if (fromCur == "GBP" && toCur == "USD") return 1.2650m;
                return 1m;
            });

            services.AddSingleton<INewsFilter>(_ => new NewsFilter());
            services.AddSingleton<SessionFilter>();
            services.AddSingleton<DrawdownTracker>();
            services.AddSingleton<RiskManager>();
            services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());

            var solutionRoot = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var configLoader = new ConfigLoader(solutionRoot);
            var loadedConfig = configLoader.Load();
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

            // Progress events (now possible because engine is in-process)
            var progressCallback = new Progress<BacktestProgressEvent>(evt =>
            {
                PushProgressEvent(runId, evt.EventType, evt.Message);
            });
            services.AddSingleton<IProgress<BacktestProgressEvent>>(_ => progressCallback);

            // Strategies
            var registry = new StrategyRegistry();
            services.AddSingleton(registry);
            services.AddSingleton<IEnumerable<IStrategy>>(sp =>
            {
                var reg = sp.GetRequiredService<StrategyRegistry>();
                var loaded = sp.GetRequiredService<LoadedConfig>();
                var activeIds = loaded.StrategyConfigs.Select(c => c.Id).ToArray();
                return reg.CreateStrategies(activeIds, loaded, sp);
            });

            services.AddSingleton<EngineWorker>(sp => new EngineWorker(
                sp.GetRequiredService<IBrokerAdapter>(),
                sp.GetRequiredService<IRiskManager>(),
                sp.GetRequiredService<DrawdownTracker>(),
                sp.GetRequiredService<IEnumerable<IStrategy>>(),
                sp.GetRequiredService<IIndicatorService>(),
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<IEngineClock>(),
                sp.GetRequiredService<ISymbolInfoRegistry>(),
                sp.GetRequiredService<IRiskProfileResolver>(),
                sp.GetRequiredService<Func<string, string, decimal>>(),
                sp.GetRequiredService<PersistenceService>(),
                sp.GetRequiredService<OrderDispatcher>(),
                sp.GetRequiredService<PositionTracker>(),
                sp.GetRequiredService<ILogger<EngineWorker>>(),
                sp.GetRequiredService<EngineRunContext>(),
                dataFeed: null,
                progress: sp.GetRequiredService<IProgress<BacktestProgressEvent>>()));
            services.AddHostedService<EngineWorker>(sp => sp.GetRequiredService<EngineWorker>());
        })
        .Build();

    // Subscribe event handlers
    var eventBus = innerHost.Services.GetRequiredService<IEventBus>();
    eventBus.Subscribe<EquityUpdated>(
        innerHost.Services.GetRequiredService<EquityPersistenceHandler>());
    eventBus.Subscribe<TradeClosed>(
        innerHost.Services.GetRequiredService<TradePersistenceHandler>());
    eventBus.Subscribe<BarEvaluated>(
        innerHost.Services.GetRequiredService<BarEvaluationHandler>());

    // Set risk rules
    var rm = innerHost.Services.GetRequiredService<RiskManager>();
    var loaded = innerHost.Services.GetRequiredService<LoadedConfig>();
    var activeRiskProfileId = loaded.StrategyConfigs
        .Select(c => c.RiskProfileId).FirstOrDefault() ?? "standard";
    var activeProfile = loaded.RiskProfiles.FirstOrDefault(r => r.Id == activeRiskProfileId);
    var activeRuleSetId = activeProfile?.PropFirmRuleSetId ?? "ftmo-standard";
    var ruleSet = loaded.PropFirms.FirstOrDefault(r => r.Id == activeRuleSetId);
    if (ruleSet is not null) rm.SetActiveRuleSet(ruleSet);

    // Start engine
    await innerHost.StartAsync(cts.Token);
    EnqueueLog(runId, logLines,
        $"[{DateTime.UtcNow:HH:mm:ss}] Engine started (in-process NetMQ).");

    // Launch ctrader-cli (only subprocess)
    var cli = new CTraderCli();
    var args = new[]
    {
        $"--start={cfg.Start:dd/MM/yyyy}", $"--end={cfg.End:dd/MM/yyyy}",
        $"--symbol={cfg.Symbol}", $"--period={cfg.Period}",
        $"--balance={cfg.Balance}", $"--commission={cfg.CommissionPerMillion}",
        $"--spread={cfg.SpreadPips}", "--data-mode=m1",
        $"--ctid={_configuration["CTrader:CtId"]}",
        $"--pwd-file={_configuration["CTrader:PwdFile"]}",
        $"--account={_configuration["CTrader:Account"]}",
        $"--DataPort={dataPort}", $"--CommandPort={commandPort}",
        $"--SymbolString={string.Join(",", cfg.Symbols)}",
        $"--Periods={string.Join(",", cfg.Periods)}",
        "--full-access",
    };

    var algoPath = /* use ResolveAlgoPath pattern or CTraderCli locator */;
    var cliResult = await cli.BacktestAsync(algoPath, args, cts.Token);

    EnqueueLog(runId, logLines,
        $"[{DateTime.UtcNow:HH:mm:ss}] CLI exit code: {cliResult.ExitCode}");

    await innerHost.StopAsync(CancellationToken.None);
    innerHost.Dispose();

    return new BacktestResult { RunId = runId, ExitCode = cliResult.ExitCode, AlgoHash = "" };
}
```

**Implementation notes**:
- Copy the DI block from `RunEngineReplayAsync` (lines 245-330 in current code)
- Replace `BacktestReplayAdapter` with `NetMQBrokerAdapter`
- Remove `IBarRepository` registration (not needed for cTrader path)
- Keep `IProgress<BacktestProgressEvent>` — the in-process engine can now push events to UI
- The engine's `RunBacktestLoopAsync` won't run in Live mode (it branches on `_engineMode`). The engine uses `Task.WhenAll` with 4 concurrent tasks for Live mode. **But** — the cBot sends bars and ticks via PUB, so the concurrent tasks work correctly in Live mode.
- `ResolveAlgoPath`: use the same logic from `BacktestRunner.ResolveAlgoPath` (Debug preferred over Release)

### B2 — Replace BacktestRunner subprocess in RunAsync

**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs`

In `RunAsync`, replace the cTrader branch:
```csharp
// BEFORE
if (useCtader)
{
    var runner = new BacktestRunner(_configuration, runnerLogger);
    result = await runner.RunAsync(cfg);
}

// AFTER
if (useCtader)
{
    result = await RunEngineNetMqAsync(runId, cfg, state.LogLines);
}
```

`BacktestRunner` is no longer used by the orchestrator. It's still used by pipeline tests
(they create it directly).

### B3 — Remove dead code from BacktestRunner (optional)

**File**: `src/TradingEngine.CTraderRunner/BacktestRunner.cs`

`StartEngine`, `WaitForEngineReadyAsync`, and `BuildCliArgs` are no longer called
from the orchestrator. Keep them for pipeline tests which use `BacktestRunner` directly.

Mark them as `[Obsolete]` or add a comment saying "Pipeline test only".

### B4 — Verify

```powershell
# Build
dotnet build --no-incremental   # 0 errors

# Verify replay still works (should not be affected)
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "ReplayBacktest"

# Verify cTrader path works via API
dotnet run --project src/TradingEngine.Web --environment Development
# POST /api/backtest/start with UseForBacktest=true
# Verify trades appear in DB
```

---

## Phase C — BUG-05: Per-bar cross-rates

**File**: `src/TradingEngine.Host/EngineWorker.cs`

The `Func<string, string, decimal>` cross-rate provider is hardcoded in multiple places
(`Host/Program.cs`, `BacktestOrchestrator.RunEngineReplayAsync`, `RunEngineNetMqAsync`).
This causes stale cross-rates for GBP/JPY pairs.

### Fix

In `RunBacktestLoopAsync`, after `SyncToBar`, update a shared cross-rate variable
that the `Func<string, string, decimal>` closure can access:

```csharp
// In the inner host DI, instead of hardcoded closure:
decimal _gbpUsdRate = 1.2650m;
decimal _jpyUsdRate = 1m / 149.50m;

services.AddSingleton<Func<string, string, decimal>>(_ => (from, to) =>
{
    if (from == "JPY" && to == "USD") return _jpyUsdRate;
    if (from == "GBP" && to == "USD") return _gbpUsdRate;
    return 1m;
}));
```

Then in `RunBacktestLoopAsync`, after `SyncToBar`, update these values based on
the current bar's close price for the cross pairs:

```csharp
// After replay.SyncToBar(bar.Close, bar.OpenTimeUtc):
// Update cross-rates based on current bar close (for EURUSD, the cross is 1.0)
// For GBPUSD backtest, the bar IS the cross rate
// For other pairs, need to read from the bar history
```

However, this requires knowing the cross-pair bar prices, which may not be loaded.
**Scope**: For Phase C, update the cross-rate only for the primary symbol being tested.
If testing GBPUSD, the cross-rate from GBP to USD IS the GBPUSD price itself.
If testing USDJPY, the cross-rate from JPY to USD is `1 / USDJPY price`.

**Simpler approach for Phase C**: In the inner host, make the cross-rate closure
capture a reference that `SyncToBar` can update. Pass the bar's close to the
cross-rate function.

Actually, the simplest approach: the `Func<string, string, decimal>` is called with
(from, to) parameters. In `RunBacktestLoopAsync`, we can update the closure's
captured variables. Let the adapter expose the current bar's close, and the cross-rate
function reads from it.

For now, defer to a follow-up if it requires significant refactoring.

---

## Phase D — Multi-symbol pipeline test completion

### D1 — Test GBPUSD via pipeline

**File**: `tests/TradingEngine.Tests.Simulation/Pipeline/FullBacktestPipelineTest.cs`

The `ResolveCredential` already reads `CTrader:Symbol` from `appsettings.Development.json`.
The test config includes `CTrader:Symbol` and `CTrader:Symbols` (added in iter-15 Phase D).

To test GBPUSD: add a new test method or parameterize existing:

```csharp
[Fact(Timeout = 120_000)]
public async Task GbpUsdH1_ThreeDays_VerifiesPipeAndDataFlow()
{
    // Same as EurUsdH1_ThreeDays but with Symbol override
}
```

Or better: add a `[Theory]` with inline data:

```csharp
[Theory]
[InlineData("EURUSD")]
[InlineData("GBPUSD")]
public async Task ThreeDays_PipeAndDataFlow(string symbol)
{
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CTrader:Symbol"] = symbol,
            ["CTrader:Symbols"] = symbol,
        })
        .Build();
    // ... use config["CTrader:Symbol"] instead of hardcoded
}
```

### D2 — Update `appsettings.Development.json` with multi-symbol comment

Add comment explaining how to switch symbols:
```json
"CTrader": {
    "Comment": "To test different symbols, change Symbol/Symbols. Supported: EURUSD, GBPUSD, USDJPY, GBPJPY, XAUUSD",
    "Symbol": "EURUSD",
    "Symbols": "EURUSD"
}
```

---

## Phase E — Remaining OPEN-ISSUES cleanup

### E1 — BUG-05 cross-rates (if not done in Phase C)

See Phase C. If deferred, mark as "Deferred to iter-17" in OPEN-ISSUES.md.

### E2 — DESIGN-02: Exec events drained on ticks

**File**: `src/TradingEngine.Host/EngineWorker.cs`

In the live cTrader path (NetMQ adapter with `Task.WhenAll`), execution events are
only drained in `ProcessTicksAsync` when a tick arrives. If no ticks arrive, fills
sit unprocessed. The replay path already has `DrainExecutionStreamAsync()` called
after each bar.

**Fix**: Add a periodic drain in `ProcessBarsAsync` for the live path, similar to
what `RunBacktestLoopAsync` does. After processing each bar, drain pending execution
events from the intermediate channel:

```csharp
// In ProcessBarsAsync, after the strategy foreach loop:
while (_executionEventChannel.Reader.TryRead(out var execEvent))
    await _positionTracker.OnExecutionAsync(execEvent, _strategies);
```

This already exists in the codebase (added in iter-13) — verify it's still there.

**Actually**: Looking at the code, this was moved to `RunBacktestLoopAsync` and the
concurrent `ProcessBarsAsync` no longer drains execution events. In Live mode, the
bar loop doesn't drain execs — they wait for ticks. **Fix**: add a drain to
`ProcessBarsAsync` in the Live path.

### E3 — DESIGN-03: Cancel() kills subprocess

**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs`

After Phase B, there's only one subprocess (ctrader-cli). Store the `CancellationTokenSource`
per run in `BacktestRunState`:

```csharp
public sealed record BacktestRunState
{
    // ... existing fields ...
    public CancellationTokenSource? CancellationSource { get; set; }
}
```

In `Cancel()`:
```csharp
public void Cancel(string runId)
{
    if (_runs.TryGetValue(runId, out var state))
    {
        state.Status = "cancelled";
        state.CancellationSource?.Cancel();
    }
}
```

In `RunAsync`, create a CTS and store it per run.

### E4 — DESIGN-07: RunAsync fire-and-forget

**File**: `src/TradingEngine.Web/Services/BacktestOrchestrator.cs`

```csharp
// Line ~85: _ = RunAsync(runId, cfg);

// Fix: Store the task
state.RunTask = RunAsync(runId, cfg);
```

In `DisposeAsync` or a new `StopAsync`, await all in-flight tasks:
```csharp
public async Task StopAllAsync()
{
    var tasks = _runs.Values.Select(s => s.RunTask).Where(t => t is not null);
    await Task.WhenAll(tasks!);
}
```

### E5 — OBS-04: Equity curve data

**File**: `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs`

The replay adapter sends account updates at the start and on every bar. The engine's
`EquityPersistenceHandler` already saves them to the DB. The equity data exists —
just needs to be exposed via the API.

**Fix**: Add `GetEquityAsync` to `IBacktestQueryService` and use it in the Detail page.
The data is already in `EquitySnapshots` table. This is a query/view fix, not a data fix.

---

## Phase F — Polish and sign-off

### F1 — Update OPEN-ISSUES.md

Mark all newly fixed issues. Add `✅ Fixed (Iteration 16)` to:
- DESIGN-02, DESIGN-03, DESIGN-07, OBS-04 (if done)
- BUG-05 (if Phase C completed)

### F2 — Create HANDOVER.md

### F3 — Full regression

```powershell
dotnet build --no-incremental                                    # 0 errors
dotnet test tests/TradingEngine.Tests.Unit --no-build            # 87/87
dotnet test tests/TradingEngine.Tests.Integration --no-build     # 15/15
dotnet test tests/TradingEngine.Tests.Simulation --no-build      # 11/11
```

### F4 — UI smoke test

```powershell
dotnet run --project src/TradingEngine.Web --environment Development

# Browser: /Backtests/Run
# Replay mode (UseForBacktest=false): submit → trades appear
# cTrader mode (UseForBacktest=true): submit → trades appear (in-process engine)
```

---

## Forbidden

- Do not change the cBot's OnStart socket order (POC-pattern, verified working)
- Do not change `NetMQBrokerAdapter.SendCommandAsync` or `OnRouterReceive`
- Do not change the identity `.ToArray()` fix (critical)
- Do not change `RunBacktestLoopAsync` (sequential replay loop)
- Do not add EF migrations
- Do not change strategy logic

---

## Estimated effort

| Phase | Effort | Risk |
|-------|--------|------|
| A (quick fixes) | 30 min | Low |
| B (in-process engine) | 2 hours | Medium — DI registration accuracy |
| C (cross-rates) | 1 hour | Medium — needs careful rate math |
| D (multi-symbol tests) | 30 min | Low |
| E (open issues) | 2 hours | Medium — touches multiple files |
| F (polish) | 30 min | Low |

---

## Handover notes

_(Implementing agent fills this section)_

### Verification results

| Check | Result |
|-------|--------|
| `dotnet build --no-incremental` | |
| Unit tests (87) | |
| Integration tests (15) | |
| Simulation tests (11) | |
| Replay UI backtest (trades > 0) | |
| cTrader UI backtest (trades > 0) | |
| EURUSD pipeline test | |
| GBPUSD pipeline test | |
| Progress page shows events in cTrader mode | |

### Issues closed

| ID | Status |
|----|--------|
| Build break (NuGet version) | |
| UI default mode (UseForBacktest) | |
| In-process cTrader engine | |
| BUG-05 (cross-rates) | |
| DESIGN-02 (exec drain on ticks) | |
| DESIGN-03 (Cancel kills subprocess) | |
| DESIGN-07 (fire-and-forget) | |
| OBS-04 (equity curve) | |

### Deviations from the plan

_(Any constructor signature differences, DI issues, etc.)_
