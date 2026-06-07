# Iteration 6 — Fix Backtest Pipeline: Pipe Probe, Process Leak, Engine Reset

> Date: 2026-06-07
> Branch: `phase/6-pipe-fix`
> Goal: One successful cTrader backtest run via Aspire + web UI, no hanging processes.

---

## Mandatory reading before touching code

`docs/WORKFLOW.md` → `DECISIONS.md` → this file. Nothing else.

---

## Root Cause Analysis (read before writing code)

### RC-1 — `PipeExists()` destroys the engine connection before cBot can use it

`src/TradingEngine.CTraderRunner/BacktestRunner.cs:130`

```csharp
private static bool PipeExists(string pipeName, int timeoutMs = 200)
{
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    client.Connect(timeoutMs);   // ← actually CONNECTS, consuming the engine's one connection slot
    return true;
}
```

The engine creates `NamedPipeServerStream` with `maxNumberOfServerInstances=1`. When `PipeExists` connects, the engine's `WaitForConnectionAsync` completes — the engine thinks the cBot has connected. When the probe client disposes, the engine sees a disconnect and enters `TryReconnectAsync` (2s+ delay). Meanwhile ctrader-cli's cBot tries to connect and finds no server. This is deterministic: it fails **every time** when Aspire is already running the engine.

### RC-2 — BacktestRunner fights Aspire for engine ownership

Aspire (`aspire/TradingEngine.AppHost/AppHost.cs`) starts the engine with `Engine__Mode=Live`. The engine is always running under Aspire.

`BacktestRunner.RunAsync()` also calls `StartEngine()` if `PipeExists` returns false — this starts a *second* engine subprocess using the same pipe name `"trading-engine"`. Two servers on the same pipe name = one creation fails silently.

**BacktestRunner must never start the engine when Aspire is managing it.** `BacktestRunner` = CLI launcher only, under Aspire.

### RC-3 — `WebSmokeTests` leaks engine processes

`tests/TradingEngine.Tests.Integration/WebSmokeTests.cs:71` — `ApiBacktestStart_ReturnsRunId` POSTs to `/api/backtest/start`. This calls `BacktestOrchestrator.Start()` which fires `_ = RunAsync(runId, cfg)` as fire-and-forget. `RunAsync` calls `BacktestRunner.RunAsync()` which calls `PipeExists` (fails → false) → `StartEngine()` → `dotnet run` subprocess launched.

The test only checks the HTTP 200 response, not the async outcome. Test finishes. The engine subprocess is still starting up. The `finally` block that kills it runs only when `RunAsync` completes — but the test process may have moved on. Result: orphan `dotnet` processes after every test run.

### RC-4 — Engine state not reset between runs

When a backtest completes and the cBot disconnects, `TryReconnectAsync` creates a new pipe server and waits. The next backtest's cBot can connect — but the engine's `_bars`, `_indicatorValues`, `_currentEquity`, `_tickCount`, `_barCount` all carry state from the previous run. Indicators computed on stale bars → wrong signals for run 2+.

---

## Phases

### Phase 6A — Gut `BacktestRunner` (fixes RC-1 and RC-2)

**File:** `src/TradingEngine.CTraderRunner/BacktestRunner.cs`

Replace the entire `RunAsync` method with this:

```csharp
public async Task<BacktestResult> RunAsync(BacktestConfig cfg, CancellationToken ct = default)
{
    var runId = Guid.NewGuid().ToString("N")[..8];
    var pipeName = _config.GetValue<string>("Engine:Broker:PipeName") ?? "trading-engine";
    var resultsDir = Path.Combine(Path.GetTempPath(), "shamshir-backtest", runId);
    Directory.CreateDirectory(resultsDir);
    var reportJsonPath = Path.Combine(resultsDir, "report.json");

    Process? engineProcess = null;
    if (_config.GetValue<bool>("CTrader:StartEngineSubprocess", false))
    {
        engineProcess = StartEngine(pipeName, runId);
        _logger.LogInformation("Engine subprocess started. PID={Pid}", engineProcess?.Id ?? -1);
        await WaitForEngineReadyAsync(pipeName, TimeSpan.FromSeconds(30), ct);
    }
    else
    {
        _logger.LogInformation("Using Aspire-managed engine. Pipe={Pipe}", pipeName);
    }

    try
    {
        var cliPath = CTraderCliLocator.Locate(_config);
        var algoPath = ResolveAlgoPath();
        var args = BuildArgs(cfg, algoPath, pipeName, reportJsonPath);

        _logger.LogInformation("Launching ctrader-cli. RunId={RunId} Args={Args}", runId, args);

        using var cliProcess = Process.Start(new ProcessStartInfo(cliPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start ctrader-cli");

        var stdoutTask = cliProcess.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = cliProcess.StandardError.ReadToEndAsync(ct);
        await cliProcess.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        _logger.LogInformation("ctrader-cli exited. Code={Code} RunId={RunId}", cliProcess.ExitCode, runId);
        if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogWarning("ctrader-cli stderr: {Stderr}", stderr);

        return ParseResult(runId, cliProcess.ExitCode, stdout, stderr, reportJsonPath);
    }
    finally
    {
        if (engineProcess is not null && !engineProcess.HasExited)
        {
            engineProcess.Kill(entireProcessTree: true);
            await engineProcess.WaitForExitAsync(CancellationToken.None);
            _logger.LogInformation("Engine subprocess killed. PID={Pid}", engineProcess.Id);
        }
    }
}
```

Delete `PipeExists()` entirely. It must not exist.

Keep `StartEngine()` and `WaitForEngineReadyAsync()` but make `WaitForEngineReadyAsync` use a **passive** pipe existence check — `File.Exists(Path.Combine(@"\\.\pipe", pipeName))`. This checks the Windows pipe namespace without connecting. Do NOT use `NamedPipeClientStream.Connect`.

```csharp
private static async Task WaitForEngineReadyAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
{
    var pipePath = Path.Combine(@"\\.\pipe", pipeName);
    var deadline = DateTime.UtcNow.Add(timeout);
    while (DateTime.UtcNow < deadline)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(pipePath))
            return;
        await Task.Delay(300, ct);
    }
    throw new TimeoutException($"Engine not ready after {timeout.TotalSeconds}s — pipe '{pipeName}' not visible");
}
```

---

### Phase 6B — Coordinate pipe name through Aspire (fixes RC-2 partially)

**File:** `aspire/TradingEngine.AppHost/AppHost.cs`

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var dbPath = Path.GetFullPath(
    Path.Combine(builder.AppHostDirectory, "..", "..", "data", "trading.db"));

const string pipeName = "trading-engine";

var engine = builder.AddProject<Projects.TradingEngine_Host>("engine")
    .WithEnvironment("Engine__Mode", "Live")
    .WithEnvironment("Engine__Broker__PipeName", pipeName)
    .WithEnvironment("Persistence__DbPath", dbPath);

var web = builder.AddProject<Projects.TradingEngine_Web>("web")
    .WithEnvironment("Persistence__DbPath", dbPath)
    .WithEnvironment("Engine__Broker__PipeName", pipeName)
    .WithEndpoint(port: 5200, scheme: "http", name: "web-http");

builder.Build().Run();
```

Both engine and web now share the same pipe name via env var. BacktestRunner reads it from `_config["Engine:Broker:PipeName"]`. No hardcoded `"trading-engine"` strings in BacktestRunner.

---

### Phase 6C — Fix WebSmokeTests process leak (fixes RC-3)

**File:** `tests/TradingEngine.Tests.Integration/WebSmokeTests.cs`

The `ApiBacktestStart_ReturnsRunId` test calls the real backtest API. Under `WebApplicationFactory`, the web's DI uses real `IConfiguration` including `BacktestRunner`. To prevent it from ever starting a subprocess in tests, override the config in the factory:

```csharp
_client = factory.WithWebHostBuilder(builder =>
{
    builder.UseSetting("Persistence:DbPath", tempDb);
    builder.UseSetting("CTrader:StartEngineSubprocess", "false");  // ← add this
    builder.UseSetting("CTrader:CliPath", "echo");                 // ← stub CLI
}).CreateClient();
```

With `CTrader:StartEngineSubprocess=false` (the new default in Phase 6A), no engine subprocess is started. With `CTrader:CliPath=echo`, the BacktestRunner calls `echo` instead of the real CLI — it exits immediately with code 0. The test gets a runId and status back without any real subprocess.

Note: `BacktestController.cs` test `ApiBacktestStart_ReturnsRunId` should only assert the HTTP shape (runId present, status present) — not that a backtest actually ran. The existing test already does this correctly.

Also add `builder.UseSetting("CTrader:CliPath", "echo")` to prevent `CTraderCliLocator.Locate()` from throwing `FileNotFoundException` when ctrader-cli is not installed on the CI machine.

---

### Phase 6D — Engine state reset on new connection (fixes RC-4)

**File:** `src/TradingEngine.Infrastructure/Adapters/NamedPipeBrokerAdapter.cs`

Add an `OnClientConnected` callback that the engine can hook into:

```csharp
public Action? OnClientConnected { get; set; }
```

In `ConnectAsync`, after `WaitForConnectionAsync` completes:
```csharp
await _pipeServer.WaitForConnectionAsync(ct);
_logger?.LogInformation("Pipe connected. PipeName={PipeName}", _pipeName);
OnClientConnected?.Invoke();   // ← new line
_ = ReadLoopAsync(_cts.Token);
```

Also in `TryReconnectAsync`, after `WaitForConnectionAsync` completes:
```csharp
await _pipeServer.WaitForConnectionAsync(ct);
_logger?.LogInformation("Pipe reconnected on attempt {Attempt}", attempt + 1);
OnClientConnected?.Invoke();   // ← new line
```

**File:** `src/TradingEngine.Host/EngineWorker.cs`

Add a `ResetState()` method:
```csharp
private void ResetState()
{
    _bars.Clear();
    _indicatorValues.Clear();
    _reusableIndicatorDict.Clear();
    _latestAccountUpdate = null;
    Volatile.Write(ref _currentEquity, new EquitySnapshot(DateTime.MinValue, 0, 0, 0, 0, 0, 0, 0, _engineMode));
    Interlocked.Exchange(ref _tickCount, 0);
    Interlocked.Exchange(ref _barCount, 0);
    _logger.LogInformation("Engine state reset for new connection");
}
```

In `ExecuteAsync`, wire up the callback after `ConnectAsync`:
```csharp
if (_broker is NamedPipeBrokerAdapter pipeAdapter)
    pipeAdapter.OnClientConnected = ResetState;

await _broker.ConnectAsync(ct);
```

This ensures a clean slate for every new cBot connection, whether it's the first or a subsequent run.

---

### Phase 6E — Verify end-to-end with Aspire (manual check, not a test)

After these changes, the user runs:
```
dotnet run --project aspire/TradingEngine.AppHost
```

In Aspire dashboard, both engine and web are green. Engine logs show: `"Pipe connected. PipeName=trading-engine"` only when the cBot actually connects — not during startup.

User opens `http://localhost:5200`, clicks Run Backtest, fills in:
- Symbol: EURUSD
- Period: H1  
- Start: 2024-01-15
- End: 2024-04-15

Clicks submit. The web UI calls `/api/backtest/start`. `BacktestRunner.RunAsync()` skips engine start (Aspire manages it), launches ctrader-cli directly.

The cBot connects to the engine pipe. Engine logs show tick/bar counts incrementing. After 55+ H1 bars (55 hours into the backtest data), strategies evaluate. Signals generate. Orders submit.

At completion, the web UI shows the run as "completed" with trade count > 0.

---

## What the sub-agent must NOT do

- Do not add complexity: no new projects, no new abstractions, no new interfaces
- Do not "improve" BacktestRunner beyond what's described — keep it as a thin CLI launcher
- Do not add more SSE streaming, log lines, or UI features
- Do not change the strategy logic or indicator code
- Do not add `dotnet build` steps inside BacktestRunner — build is the user's responsibility

## Tests

The 106 existing tests must still pass after these changes.

Add exactly **two** new tests:

**T-1** (`TradingEngine.Tests.Integration/WebSmokeTests.cs`): `ApiBacktestStart_ReturnsRunId` should work with the new `CTrader:CliPath=echo` override — verify it still passes and does not leave orphan processes.

**T-2** (`TradingEngine.Tests.Unit/`): `BacktestRunner_DoesNotStartEngine_WhenSubprocessDisabled`  
```csharp
var config = new ConfigurationBuilder()
    .AddInMemoryCollection(new[] {
        new KeyValuePair<string, string?>("CTrader:StartEngineSubprocess", "false"),
        new KeyValuePair<string, string?>("CTrader:CliPath", "echo"),
        new KeyValuePair<string, string?>("Engine:Broker:PipeName", "test-pipe"),
    }).Build();
var logger = Substitute.For<ILogger<BacktestRunner>>();
var runner = new BacktestRunner(config, logger);
// Should run without starting any subprocess
// PipeExists should NOT be called
```

This test mostly documents the contract — BacktestRunner skips engine start when disabled.

---

## Decisions to Record

| ID | Decision |
|---|---|
| D65 | BacktestRunner never probes the pipe; `PipeExists()` removed permanently |
| D66 | BacktestRunner does not start engine subprocess by default (`CTrader:StartEngineSubprocess=false`); Aspire owns engine lifecycle |
| D67 | Aspire AppHost sets `Engine__Broker__PipeName` on both engine and web; BacktestRunner reads it from config |
| D68 | EngineWorker resets state (bars, indicators, equity) on each new pipe client connection via `OnClientConnected` callback |
| D69 | WebSmokeTests overrides `CTrader:CliPath=echo` and `CTrader:StartEngineSubprocess=false` to prevent subprocess leaks |

---

## Definition of Done

- [ ] `dotnet build TradingEngine.slnx` — 0 errors, 0 warnings
- [ ] `dotnet test TradingEngine.slnx` — all 106+ tests pass, no orphan processes left after test run
- [ ] `BacktestRunner.cs` contains no `PipeExists` method and no `NamedPipeClientStream` usage
- [ ] Aspire `AppHost.cs` sets `Engine__Broker__PipeName` on both engine and web
- [ ] Manual smoke: `dotnet run --project aspire/TradingEngine.AppHost` → engine starts → web opens → Run Backtest → ctrader-cli connects to engine pipe → cBot sends ticks → engine processes → backtest completes without "no pipe found" error
- [ ] DECISIONS.md updated with D65–D69
