# Shamshir — Iteration 4 Brief

> Prepared: 2026-06-06
> Scope: Money management circuit fixes + cTrader CLI integration + basic backtest results in web UI
> Prerequisites: Read docs/WORKFLOW.md → DECISIONS.md → this file → PHASE4A-MONEY-MGMT-BRIEF.md

---

## Mandatory Reading Before Starting

1. `docs/WORKFLOW.md` — process rules, code standards, DoD
2. `DECISIONS.md` — full decision record (D1–D59)
3. `PHASE4A-MONEY-MGMT-BRIEF.md` — complete spec for Phase 4A (exact code included)
4. `ITERATION-3-FINAL.md §2` — bug list that 4A fixes

---

## Iteration Goals

1. **Engine starts and runs correctly** — the money management circuit must work end-to-end:
   wins and losses must flow through to drawdown state, and risk gates must fire.
2. **cTrader CLI backtest runs programmatically** — the orchestrator invokes `ctrader-cli backtest`,
   the cBot connects to the engine via named pipe, and results are recorded in SQLite.
3. **Web UI shows backtest runs** — a minimal `/backtests` page lists completed runs with summary metrics.
4. **Auto-deploy pipeline** — building the cBot project copies the `.algo` file to cTrader's sources dir
   when a config flag is set.

---

## Phase Order

```
4A (money mgmt fixes)
  → 11A (cBot migration + bug fixes)    ← can start in parallel with 4A if needed
      → 11B (CTraderRunner orchestrator)
          → 11C (DB + backtest tracking)
              → 11D (web UI — stretch goal)
              → 11E (auto-deploy — low effort, do early if 11A is unblocked)
```

**4A must complete before the engine is usable in any subsequent phase.**
**11A must complete before the CLI backtest can run.**

---

## Phase 4A — Money Management Circuit

**Branch:** `phase/4a-money-management`
**Full spec:** `PHASE4A-MONEY-MGMT-BRIEF.md` — read it in full before starting. It contains
exact corrected code for every fix. Do not deviate from it.

### Summary of fixes (all detailed in PHASE4A brief):

| Fix | File | Description |
|-----|------|-------------|
| C-4 | `SimulatedBrokerAdapter.cs` | Emit `AccountUpdate` on every fill and close; update `_currentBalance` |
| C-1 | `EngineWorker.cs:139` | Use resolved `profile` in `CalculateLotSize`, not hardcoded inline object |
| C-5 | `Program.cs:113` | `PersistenceService` → `AddSingleton`; use `IServiceScopeFactory` internally |
| S-1 | `DrawdownTracker.cs:34` | Daily DD base = `InitialAccountBalance`, not `DailyStartEquity` (FTMO rule) |
| S-2 | `Program.cs` + `EngineWorker.cs` | Call `SetActiveRuleSet()` — currently never called; risk gates silently skip |
| Lag | `EngineWorker.cs:HandleAccountUpdate` | Call `UpdateEquityLevels(rawEquity)` first, then build snapshot from fresh state |
| New | `IRiskManager` | Add `UpdateEquityLevels(decimal rawEquity)` method; remove old `OnEquityUpdate(EquitySnapshot)` |

### Tests required (all specified in PHASE4A brief §6):
- `DrawdownTrackerTests` — 14 tests including FTMO-base assertions
- `RiskManagerTests` — gate tests + full circuit test
- `SimulatedBrokerTests` — AccountUpdate emission on fill and close
- `DrawdownScenarios` in Simulation project — 5 end-to-end scenarios
- `PositionSizerTests` — 5 tests

### Phase 4A DoD:
- `dotnet build TradingEngine.sln` — 0 errors
- `dotnet test TradingEngine.sln` — all pass, count ≥ 69 + new 4A tests
- `dotnet run --project src/TradingEngine.Host` — no startup exception
- Log shows "Account update processed" during backtest run
- Lot sizes are non-zero in backtest log after first AccountUpdate

---

## Phase 11A — cBot Migration + Bug Fixes

**Branch:** `phase/11a-cbot-migration`

### Critical: Retarget to net6.0

cTrader CLI will not load net48 `.algo` files. This is non-optional.

```xml
<!-- src/TradingEngine.Adapters.CTrader/TradingEngine.Adapters.CTrader.csproj -->
<PropertyGroup>
  <TargetFramework>net6.0</TargetFramework>
  <LangVersion>10</LangVersion>       <!-- upgrade from C# 6 — external IDE flow allows it -->
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="cTrader.Automate" Version="*" />
  <PackageReference Include="Newtonsoft.Json" Version="13.*" />
</ItemGroup>
```

After this change, `dotnet build` will produce `src.algo` in the output directory.
Verify: `ctrader-cli metadata path/to/src.algo` shows the cBot metadata.

### Add [Parameter] for pipe name (required for orchestrator)

```csharp
[Parameter("Pipe Name", DefaultValue = "trading-engine")]
public string PipeName { get; set; } = "trading-engine";

[Parameter("Transport", DefaultValue = "pipe")]
public string Transport { get; set; } = "pipe";   // reserved for future TCP mode
```

Wire `PipeName` into `PipeClient` constructor in `OnStart()`:
```csharp
_pipe = new PipeClient(PipeName);
```

### Fix B-1: HandleModifyOrder modifies volume, not SL/TP

```csharp
// BEFORE (wrong — modifies volume)
_robot.ModifyPosition(pos, pos.VolumeInUnits);

// AFTER (correct — modifies SL and TP)
var data = MessageSerializer.Deserialize<ModifyOrderData>(payload);
foreach (var pos in _robot.Positions)
{
    if (pos.Id.ToString() == data.PositionId.ToString())
    {
        var slPips = PriceToPips(data.NewStopLoss, _robot.Symbols.GetSymbol(pos.SymbolName));
        var tpPips = data.NewTakeProfit > 0
            ? PriceToPips(data.NewTakeProfit, _robot.Symbols.GetSymbol(pos.SymbolName))
            : (double?)null;
        _robot.ModifyPosition(pos, slPips, tpPips);
        break;
    }
}
```

### Fix B-2: HandleClosePosition matches by ID, not by first-with-label

```csharp
var data = MessageSerializer.Deserialize<ClosePositionData>(payload);
foreach (var pos in _robot.Positions)
{
    if (pos.Id.ToString() == data.PositionId.ToString())
    {
        var result = _robot.ClosePosition(pos);
        if (result?.IsSuccessful == true)
            _accountPublisher.Publish(Account.Balance, Account.Equity,
                Account.Equity - Account.Balance, DateTime.UtcNow);
        break;
    }
}
```

### Fix B-3: SendInitialState sends real position IDs

```csharp
foreach (var pos in Positions)
{
    _executionPublisher.Publish(
        Guid.Parse(pos.Id.ToString()),  // real cTrader position ID
        "Filled",
        pos.EntryPrice,
        pos.VolumeInUnits / 100000.0,
        null,
        pos.EntryTime);
}
```

### Fix B-4: OnBar maps all required timeframes, not hardcoded H1

Add a `[Parameter]` for timeframe, or map from strategy config. For now, publish the
bar for whatever timeframe cTrader calls `OnBar` on (determined by the `--period` CLI arg):

```csharp
protected override void OnBar()
{
    if (!_running) return;
    var bars = MarketData.GetBars(TimeFrame, SymbolName);
    if (bars == null || bars.Count == 0) return;
    var last = bars.Last(1);
    _barPublisher.Publish(SymbolName, TimeFrame.ShortName, last.OpenTime,
        last.Open, last.High, last.Low, last.Close, (long)last.TickVolume);
}
```

(`TimeFrame` is the cBot's own configured timeframe, not hardcoded.)

### Fix B-5: PriceToPips uses direction-aware price

```csharp
private static double? PriceToPips(double absolutePrice, Symbol symbol)
{
    if (absolutePrice <= 0) return null;
    // Use mid-price as reference for both directions
    var mid = (symbol.Bid + symbol.Ask) / 2.0;
    return Math.Abs(absolutePrice - mid) / symbol.PipSize;
}
```

### Fix B-6: RetryConnect — resume running after successful reconnect

```csharp
public void RetryConnect()
{
    for (var i = 0; i < MaxRetries; i++)
    {
        Thread.Sleep(RetryDelays[i]);
        if (Connect(5000))
        {
            _running = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true };
            _readThread.Start();
            SendInitialState();
            Print($"Reconnected to Shamshir engine after {i + 1} attempt(s).");
            return;
        }
    }
    Print("Failed to reconnect after 3 attempts. cBot running without engine.");
}
```

### Fix B-7: Subscribe to OnPositionOpened / OnPositionClosed

```csharp
protected override void OnStart()
{
    // ... existing setup ...
    Positions.Opened += OnPositionOpened;
    Positions.Closed += OnPositionClosed;
}

private void OnPositionOpened(PositionOpenedEventArgs args)
{
    if (!_running) return;
    _executionPublisher.Publish(
        Guid.Parse(args.Position.Id.ToString()), "Filled",
        args.Position.EntryPrice, args.Position.VolumeInUnits / 100000.0,
        null, args.Position.EntryTime);
    _accountPublisher.Publish(Account.Balance, Account.Equity,
        Account.Equity - Account.Balance, DateTime.UtcNow);
}

private void OnPositionClosed(PositionClosedEventArgs args)
{
    if (!_running) return;
    _executionPublisher.Publish(
        Guid.Parse(args.Position.Id.ToString()), "Filled",
        args.Position.EntryPrice, args.Position.VolumeInUnits / 100000.0,
        null, Server.TimeInUtc);
    _accountPublisher.Publish(Account.Balance, Account.Equity,
        Account.Equity - Account.Balance, DateTime.UtcNow);
}
```

### Phase 11A DoD:
- `dotnet build src/TradingEngine.Adapters.CTrader` → 0 errors, produces `src.algo`
- `ctrader-cli metadata src/TradingEngine.Adapters.CTrader/bin/Release/net6.0/src.algo` → shows `PipeName` parameter
- All B-1 through B-7 bugs resolved

---

## Phase 11B — CTraderRunner Orchestrator

**Branch:** `phase/11b-ctrader-runner`

### New project

Add to solution:
```
src/TradingEngine.CTraderRunner/
├── TradingEngine.CTraderRunner.csproj   (net10.0, references Domain + Infrastructure)
├── CTraderCliLocator.cs
├── BacktestConfig.cs
├── BacktestResult.cs
├── BacktestEventsParser.cs
├── BacktestRunner.cs
└── GlobalUsings.cs
```

### CTraderCliLocator — auto-discover exe, config override

```csharp
public static class CTraderCliLocator
{
    public static string Locate(IConfiguration config)
    {
        // 1. Config override wins
        var configured = config["CTrader:CliPath"];
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        // 2. Auto-discover by globbing %LOCALAPPDATA%\Spotware\cTrader\**\ctrader-cli.exe
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var spotwarePath = Path.Combine(localAppData, "Spotware", "cTrader");
        if (!Directory.Exists(spotwarePath))
            throw new FileNotFoundException("cTrader installation not found. Set CTrader:CliPath in config.");

        var found = Directory.EnumerateFiles(spotwarePath, "ctrader-cli.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return found ?? throw new FileNotFoundException(
            "ctrader-cli.exe not found under AppData\\Spotware\\cTrader. Set CTrader:CliPath in config.");
    }
}
```

Add to `appsettings.json` in both Host and Web projects:
```json
{
  "CTrader": {
    "CliPath": null,
    "CtId": "",
    "PwdFile": "",
    "Account": "",
    "AlgoPath": null,
    "AutoDeploy": false,
    "SourcesPath": null
  }
}
```

`CtId`, `PwdFile`, `Account` are sensitive — load from environment variables or `secrets.json`
(gitignored). Never commit them. The orchestrator reads them from `IConfiguration` which
handles the override chain.

### BacktestConfig record

```csharp
public sealed record BacktestConfig
{
    public required string Symbol { get; init; }
    public required string Period { get; init; }     // "h1", "m15", etc.
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public decimal Balance { get; init; } = 100_000;
    public double CommissionPerMillion { get; init; } = 30;
    public double SpreadPips { get; init; } = 1;
    public string DataMode { get; init; } = "m1";
    public string? DataFile { get; init; }           // null = download from cTrader
    public string? DataDir { get; init; }            // persist downloaded data
    public Dictionary<string, string> CustomParams { get; init; } = new();
}
```

### BacktestRunner

```csharp
public sealed class BacktestRunner(IConfiguration config, ILogger<BacktestRunner> logger)
{
    public async Task<BacktestResult> RunAsync(BacktestConfig cfg, CancellationToken ct = default)
    {
        var cliPath = CTraderCliLocator.Locate(config);
        var algoPath = ResolveAlgoPath(config);
        var runId = Guid.NewGuid().ToString("N")[..8];
        var pipeName = $"trading-engine-{runId}";
        var resultsDir = Path.Combine(Path.GetTempPath(), "shamshir-backtest", runId);
        Directory.CreateDirectory(resultsDir);
        var reportJsonPath = Path.Combine(resultsDir, "report.json");

        var args = BuildArgs(cfg, algoPath, pipeName, reportJsonPath, config);
        logger.LogInformation("Starting ctrader-cli backtest. RunId={RunId} Symbol={Symbol} Period={Period}",
            runId, cfg.Symbol, cfg.Period);

        var psi = new ProcessStartInfo(cliPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ctrader-cli");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        logger.LogInformation("ctrader-cli exited. Code={Code}", process.ExitCode);
        if (!string.IsNullOrWhiteSpace(stderr))
            logger.LogWarning("ctrader-cli stderr: {Stderr}", stderr);

        return ParseResult(runId, reportJsonPath, algoPath, stdout, process.ExitCode);
    }

    private static string BuildArgs(BacktestConfig cfg, string algoPath, string pipeName,
        string reportJsonPath, IConfiguration config)
    {
        var sb = new StringBuilder();
        sb.Append($"backtest \"{algoPath}\"");
        sb.Append($" --start={cfg.Start:dd/MM/yyyy}");
        sb.Append($" --end={cfg.End:dd/MM/yyyy}");
        sb.Append($" --symbol={cfg.Symbol}");
        sb.Append($" --period={cfg.Period}");
        sb.Append($" --balance={cfg.Balance}");
        sb.Append($" --commission={cfg.CommissionPerMillion}");
        sb.Append($" --spread={cfg.SpreadPips}");
        sb.Append($" --data-mode={cfg.DataMode}");

        if (!string.IsNullOrEmpty(cfg.DataDir))
            sb.Append($" --data-dir=\"{cfg.DataDir}\"");
        if (!string.IsNullOrEmpty(cfg.DataFile))
            sb.Append($" --data-file=\"{cfg.DataFile}\"");

        sb.Append($" --report-json=\"{reportJsonPath}\"");
        sb.Append($" --ctid={config["CTrader:CtId"]}");
        sb.Append($" --pwd-file=\"{config["CTrader:PwdFile"]}\"");
        sb.Append($" --account={config["CTrader:Account"]}");
        sb.Append($" --PipeName={pipeName}");
        sb.Append(" --exit-on-stop");

        foreach (var (key, value) in cfg.CustomParams)
            sb.Append($" --{key}={value}");

        return sb.ToString();
    }

    private static string ResolveAlgoPath(IConfiguration config)
    {
        var configured = config["CTrader:AlgoPath"];
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        // Default: look in cBot project output
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Release", "net6.0", "src.algo")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Debug", "net6.0", "src.algo")),
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "src.algo not found. Build TradingEngine.Adapters.CTrader first, or set CTrader:AlgoPath.");
    }

    private static BacktestResult ParseResult(string runId, string reportJsonPath,
        string algoPath, string stdout, int exitCode) { /* see Phase 11C */ }
}
```

### Phase 11B DoD:
- `BacktestRunner` compiles and can be instantiated
- `CTraderCliLocator.Locate()` finds `ctrader-cli.exe` on the dev machine automatically
- A simple console test (or xUnit integration test) invokes `BacktestRunner.RunAsync` with a
  5-day EURUSD H1 backtest and gets a non-null result

---

## Phase 11C — DB Changes + Backtest Result Storage

**Branch:** `phase/11c-backtest-db`

### New EF Core entity

```csharp
// TradingEngine.Infrastructure/Persistence/Entities/BacktestRunEntity.cs
public sealed class BacktestRunEntity
{
    public string RunId { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public string Symbol { get; set; } = "";
    public string Period { get; set; } = "";
    public DateTime BacktestFrom { get; set; }
    public DateTime BacktestTo { get; set; }
    public decimal InitialBalance { get; set; }
    public decimal NetProfit { get; set; }
    public decimal MaxDrawdownPct { get; set; }
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public double WinRatePct { get; set; }
    public int ExitCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ReportJsonPath { get; set; }   // path to raw report for drill-down
    public string? EventsJsonPath { get; set; }   // path to events.json for trade-level data
}
```

Add to `TradingDbContext.OnModelCreating`:
```csharp
modelBuilder.Entity<BacktestRunEntity>(e =>
{
    e.ToTable("BacktestRuns");
    e.HasKey(x => x.RunId);
});
```

### BacktestEventsParser — parse cTrader's events.json

cTrader events.json path: `{algo-dir}/data/{cBotName}/{InstanceId}/Backtesting/events.json`
The InstanceId is generated by cTrader — find it by listing the data directory after the run.

```csharp
public static class BacktestEventsParser
{
    public sealed record TradeEvent(
        int Serial, long Time, string Event,
        double EntryPrice, double? ClosePrice,
        double GrossProfit, double Equity,
        string Type, double Quantity);

    public static IReadOnlyList<TradeEvent> Parse(string eventsJsonPath)
    {
        var json = File.ReadAllText(eventsJsonPath);
        return JsonSerializer.Deserialize<List<TradeEvent>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? [];
    }

    public static string? FindEventsJson(string algoDirectory, string cBotName)
    {
        var dataRoot = Path.Combine(Path.GetDirectoryName(algoDirectory)!, "data", cBotName);
        if (!Directory.Exists(dataRoot)) return null;

        return Directory.EnumerateFiles(dataRoot, "events.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
```

### IBacktestRunRepository

```csharp
public interface IBacktestRunRepository
{
    Task SaveAsync(BacktestRunEntity run, CancellationToken ct);
    Task<IReadOnlyList<BacktestRunEntity>> GetAllAsync(CancellationToken ct);
    Task<BacktestRunEntity?> GetByIdAsync(string runId, CancellationToken ct);
}
```

### BacktestResult record (completes Phase 11B's ParseResult)

```csharp
public sealed record BacktestResult
{
    public string RunId { get; init; } = "";
    public int ExitCode { get; init; }
    public bool Success => ExitCode == 0;
    public decimal NetProfit { get; init; }
    public decimal MaxDrawdownPct { get; init; }
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public double WinRatePct { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<BacktestEventsParser.TradeEvent> Events { get; init; } = [];
}
```

### Phase 11C DoD:
- `dotnet ef migrations add AddBacktestRuns` runs without error
- `BacktestRunner.RunAsync` saves a `BacktestRunEntity` to SQLite after completion
- `IBacktestRunRepository.GetAllAsync()` returns the saved run

---

## Phase 11D — Web UI: Backtest Results Page (Stretch Goal)

**Branch:** `phase/11d-web-backtests`

Only do this if 11A + 11B + 11C are complete and working. Do not start 11D with any
broken predecessor.

### New Razor Page: /backtests

```csharp
// src/TradingEngine.Web/Pages/Backtests/Index.cshtml.cs
public sealed class BacktestsIndexModel(IBacktestRunRepository repo) : PageModel
{
    public IReadOnlyList<BacktestRunEntity> Runs { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
        => Runs = await repo.GetAllAsync(ct);
}
```

Table showing: RunId (truncated), Symbol, Period, From–To, Net Profit, Max DD%, Win Rate, Trades, Status.
Link each row to `/backtests/{runId}` for the detail page (out of scope for iteration 4).

No charts in iteration 4. A plain HTML table is sufficient for the stretch goal.

### Phase 11D DoD (stretch):
- `/backtests` page loads without error
- Shows at least one row from a completed backtest run
- Linked in the nav alongside Dashboard, Trades, Performance

---

## Phase 11E — Auto-Deploy Pipeline

**Branch:** `phase/11e-auto-deploy`
This can be worked alongside 11A — it only touches the `.csproj` and a config entry.

### MSBuild target in cBot project

```xml
<!-- src/TradingEngine.Adapters.CTrader/TradingEngine.Adapters.CTrader.csproj -->
<Target Name="AutoDeployAlgo" AfterTargets="Build"
        Condition="'$(AutoDeploy)' == 'true' AND '$(CTraderSourcesPath)' != ''">
  <Copy SourceFiles="$(OutputPath)src.algo"
        DestinationFolder="$(CTraderSourcesPath)"
        OverwriteReadOnlyFiles="true" />
  <Message Text="[Shamshir] Deployed src.algo → $(CTraderSourcesPath)" Importance="high" />
</Target>
```

Invoke from CI or dev shell:
```powershell
dotnet build src/TradingEngine.Adapters.CTrader `
  --configuration Release `
  -p:AutoDeploy=true `
  -p:CTraderSourcesPath="$env:USERPROFILE\Documents\cAlgo\Sources\Robots"
```

### GitHub Actions — add build-algo job to `pr.yml`

```yaml
build-cbot:
  runs-on: windows-latest
  steps:
    - uses: actions/checkout@v4
    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '6.x'        # cBot targets net6.0
    - run: dotnet build src/TradingEngine.Adapters.CTrader --configuration Release
    - uses: actions/upload-artifact@v4
      with:
        name: cbot-algo
        path: src/TradingEngine.Adapters.CTrader/bin/Release/net6.0/src.algo
```

The `.algo` artifact is available for download from every PR build. In prod, a human
downloads it and deploys manually. `AutoDeploy: false` is the safe default.

### Phase 11E DoD:
- `dotnet build` with `-p:AutoDeploy=true` copies the file without error
- CI job produces `cbot-algo` artifact
- `CTrader:AutoDeploy: false` in committed `appsettings.json`

---

## New Decisions in This Iteration (D51–D59)

These are already resolved. Record them in DECISIONS.md as you implement:

| ID | Decision | Value |
|----|----------|-------|
| D51 | `DailyDdBase` enum for non-FTMO prop firms | Deferred — FTMO (InitialBalance) is the only supported base. Add `DailyDdBase` enum when a second prop firm config is onboarded |
| D52 | cBot target framework | `net6.0` — mandatory for cTrader CLI (CLI rejects net48 algo files) |
| D53 | ctrader-cli.exe discovery | Auto-glob `%LOCALAPPDATA%\Spotware\cTrader\**\ctrader-cli.exe`, take newest. Config override: `CTrader:CliPath` |
| D54 | Pipe transport for CLI backtest | Named pipe (Windows). TCP deferred to Phase 12 (Docker/CI Linux) |
| D55 | CTraderRunner project | New project `src/TradingEngine.CTraderRunner` (net10.0). Not a test project — it's a runtime library used by tests and tooling |
| D56 | Backtest results storage | `BacktestRuns` table in existing SQLite via `TradingDbContext`. `BacktestRunEntity` keyed by `RunId` (string GUID) |
| D57 | Web UI backtest page scope in iter 4 | Table only — no charts, no drill-down detail page. Charts and trade-level detail in iteration 5 |
| D58 | Auto-deploy mechanism | MSBuild `AfterTargets="Build"` target, gated by `-p:AutoDeploy=true`. Off by default. CI uploads artifact; prod deploys manually |
| D59 | Phase 4D merged into 4C | Lot sizing variants (`LotSizingMethod` enum, `PositionSizer` dispatch) implemented in same branch as strategy composition (4C+4D combined) |

---

## Credentials — Do Not Commit

The following must live in environment variables or `secrets.json` (gitignored):

```json
// src/TradingEngine.CTraderRunner/secrets.json  — add to .gitignore
{
  "CTrader": {
    "CtId": "your-ctid@email.com",
    "PwdFile": "C:\\path\\to\\password.pwd",
    "Account": "your-account-number"
  }
}
```

Add `**/secrets.json` to `.gitignore` if not already there.

---

## Iteration 4 DoD

All of the following must be true before writing the handover:

- [ ] `dotnet build TradingEngine.sln` — 0 errors
- [ ] `dotnet test TradingEngine.sln` — all pass, count exceeds iteration 3's 69
- [ ] Phase 4A: Engine starts without exception; equity circuit works; DD gates fire
- [ ] Phase 11A: `ctrader-cli metadata src.algo` shows `PipeName` parameter
- [ ] Phase 11B: `BacktestRunner.RunAsync` invokes `ctrader-cli backtest` and gets a result
- [ ] Phase 11C: `BacktestRuns` table exists in SQLite; completed run is persisted
- [ ] Phase 11D: (stretch) `/backtests` page shows at least one run
- [ ] Phase 11E: CI produces `cbot-algo` artifact on every PR
- [ ] No secrets or credentials in any committed file
- [ ] `ITERATION-4-HANDOVER.md` written

---

## What Is NOT In Scope for Iteration 4

Do not implement these — they are iteration 5:

- Strategy composition interfaces (ISignalProvider, IEntryFilter, etc.) — Phase 4C
- New strategies (EmaAlignment, MeanReversion, SessionBreakout) — Phase 4C
- Lot sizing variants (Kelly, AntiMartingale) — Phase 4C+4D
- OrderDispatcher + PositionTracker wiring into EngineWorker — Phase 4B
- PositionLifecycleState tracking — Phase 4E
- Startup broker reconciliation for live mode — Phase 4E
- TCP transport for Docker/CI backtest — Phase 12
- Backtest detail page with charts — Phase 11 iteration 5
- Second prop firm ruleset / DailyDdBase enum — Phase 12
