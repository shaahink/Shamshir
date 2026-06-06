# Shamshir Trading Engine — Agent Implementation Guide

> **Who this is for:** An AI coding agent (DeepSeek or equivalent) implementing this engine from scratch.
> Read `trading-engine-design-v1.md` and `trading-domain-knowledge.md` in full before touching this file.
> This guide fills gaps in those documents and adds task breakdown, validation gates, and architectural rules.
> Do not deviate from decisions marked **LOCKED**. Do not skip validation gates.

---

## 0. How to Use This Guide

Work through phases in order. Each phase has:
- A file checklist — every file that must exist when the phase is done
- A validation gate — exact commands to run; all must pass before moving on
- A mistakes section — common errors to avoid in that phase

Never start a phase until the previous phase's validation gate passes cleanly.
If a gate fails, fix it before proceeding — do not carry forward broken state.

When a section says "see design doc §N", refer to `trading-engine-design-v1.md` section N.
When a section says "see domain doc §N", refer to `trading-domain-knowledge.md` section N.

---

## 1. Known Bugs and Ambiguities in the Design Doc

Fix these immediately; do not replicate them.

### 1.1 `Position.FloatingPnL()` hardcodes contract size

**Bug:** The `Position` record in design doc §3.3 has:
```csharp
public decimal FloatingPnL(decimal currentPrice) =>
    Direction == TradeDirection.Long
        ? (currentPrice - EntryPrice.Value) * Lots * 100_000m  // ← WRONG
        : (EntryPrice.Value - currentPrice) * Lots * 100_000m;
```
`100_000` is the forex contract size. Gold (`XAUUSD`) has contract size 100. Indices and crypto have contract size 1.

**Fix:** Remove `FloatingPnL` from the `Position` record entirely.
Floating PnL is computed by `PipCalculator.FloatingPnL()` (domain doc §9.2) which uses `SymbolInfo.ContractSize`.
The `Position` record is a data container — it does not compute PnL.

### 1.2 `PositionManagementConfig` is undefined

Referenced in `IPositionManager.RegisterPosition` but never specified. Define it as:

```csharp
public record PositionManagementConfig(
    string StrategyId,
    TrailingConfig TrailingStop,
    bool UseBreakeven,
    double BreakevenTriggerR,   // R multiple at which to move SL to entry
    Pips BreakevenBufferPips,
    Money InitialRiskAmount);   // locked in at entry for R-multiple tracking
```

### 1.3 `IEngineClock` missing `BrokerTimeUtc` on `IBrokerAdapter`

Design doc §4.7 shows `BrokerClock` referencing `adapter.BrokerTimeUtc`, but `IBrokerAdapter` in §4.1 does not expose it. Add to `IBrokerAdapter`:

```csharp
DateTime BrokerTimeUtc { get; }
```

### 1.4 `SlMethod` enum mismatch

Design doc §10.2 defines `SlMethod { FixedPips, AtrMultiple, SwingHigh, SwingLow }` as four values,
but domain doc §5.3 `SwingBased()` uses `TradeDirection` to pick high vs low.
Collapse to three values:

```csharp
public enum SlMethod { FixedPips, AtrMultiple, SwingBased }
```

The direction is already on the `TradeIntent` — `SwingHigh`/`SwingLow` are redundant.

### 1.5 FTMO `dailyResetTimeUtc` conflict

`trading-domain-knowledge.md §11.2` says `"22:00:00"` UTC.
`trading-engine-design-v1.md §6.3` says `"00:00:00"`.
**Use `"22:00:00"` UTC** (= midnight Prague CET). The domain doc is the authoritative source for financial rules.

---

## 2. Filled Specifications

### 2.1 NuGet Package Versions

Use these in `Directory.Packages.props`. Pin to major.minor; allow patch updates via `*`.

```xml
<Project>
  <ItemGroup>
    <!-- Core -->
    <PackageVersion Include="Microsoft.Extensions.Hosting"                Version="10.*" />
    <PackageVersion Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.*" />

    <!-- EF Core + SQLite -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore"               Version="10.*" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite"        Version="10.*" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design"        Version="10.*" />

    <!-- Dapper -->
    <PackageVersion Include="Dapper"                                      Version="2.*" />
    <PackageVersion Include="Microsoft.Data.Sqlite"                       Version="10.*" />

    <!-- Serilog -->
    <PackageVersion Include="Serilog"                                     Version="4.*" />
    <PackageVersion Include="Serilog.Extensions.Hosting"                  Version="8.*" />
    <PackageVersion Include="Serilog.Sinks.Console"                       Version="6.*" />
    <PackageVersion Include="Serilog.Sinks.File"                          Version="6.*" />
    <PackageVersion Include="Serilog.Settings.Configuration"              Version="9.*" />

    <!-- Indicators -->
    <PackageVersion Include="Skender.Stock.Indicators"                    Version="2.*" />

    <!-- Aspire -->
    <PackageVersion Include="Aspire.Hosting.AppHost"                      Version="9.*" />
    <PackageVersion Include="Microsoft.Extensions.ServiceDiscovery"       Version="9.*" />
    <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.*" />

    <!-- Testing -->
    <PackageVersion Include="xunit"                                        Version="2.*" />
    <PackageVersion Include="xunit.runner.visualstudio"                    Version="2.*" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk"                       Version="17.*" />
    <PackageVersion Include="FluentAssertions"                             Version="7.*" />
    <PackageVersion Include="NSubstitute"                                  Version="5.*" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.InMemory"       Version="10.*" />

    <!-- Serialisation (pipe protocol + config) -->
    <PackageVersion Include="System.Text.Json"                             Version="10.*" />
  </ItemGroup>
</Project>
```

### 2.2 `Directory.Build.props` — Enforced Analyser Rules

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <Deterministic>true</Deterministic>
  </PropertyGroup>

  <!-- Prevent Domain from referencing infra packages -->
  <!-- Applied only to Domain project via condition in that project's csproj -->
</Project>
```

In `TradingEngine.Domain.csproj`, add:
```xml
<ItemGroup>
  <!-- Whitelist: only system + Serilog.Abstractions allowed -->
  <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.*" />
</ItemGroup>
```

**Rule:** If `TradingEngine.Domain.csproj` contains a `<ProjectReference>` to any other engine project, or a `<PackageReference>` to EF Core, Skender, Serilog (non-abstractions), Dapper, or System.Data — that is an architectural violation. The build must fail.

### 2.3 Solution Scaffolding Commands

Run these in order from the repo root to create the solution skeleton:

```powershell
dotnet new sln -n TradingEngine

# Source projects
dotnet new classlib -n TradingEngine.Domain         -o src/TradingEngine.Domain         --framework net10.0
dotnet new classlib -n TradingEngine.Application    -o src/TradingEngine.Application    --framework net10.0
dotnet new classlib -n TradingEngine.Risk           -o src/TradingEngine.Risk           --framework net10.0
dotnet new classlib -n TradingEngine.Services       -o src/TradingEngine.Services       --framework net10.0
dotnet new classlib -n TradingEngine.Strategies     -o src/TradingEngine.Strategies     --framework net10.0
dotnet new classlib -n TradingEngine.Infrastructure -o src/TradingEngine.Infrastructure --framework net10.0
dotnet new worker   -n TradingEngine.Host           -o src/TradingEngine.Host           --framework net10.0
dotnet new web      -n TradingEngine.Web            -o src/TradingEngine.Web            --framework net10.0

# cTrader adapter — C# 6, separate target
dotnet new classlib -n TradingEngine.Adapters.CTrader -o src/TradingEngine.Adapters.CTrader --framework net48
# Manually set <LangVersion>6</LangVersion> in its csproj after creation

# Aspire host
dotnet new aspire-apphost -n TradingEngine.AppHost  -o aspire/TradingEngine.AppHost

# Test projects
dotnet new xunit -n TradingEngine.Tests.Unit        -o tests/TradingEngine.Tests.Unit
dotnet new xunit -n TradingEngine.Tests.Integration -o tests/TradingEngine.Tests.Integration
dotnet new xunit -n TradingEngine.Tests.Simulation  -o tests/TradingEngine.Tests.Simulation

# Add all to solution
dotnet sln add (Get-ChildItem -Recurse -Filter "*.csproj" | Select-Object -ExpandProperty FullName)
```

After scaffolding, add project references per the dependency rules in design doc §2.

### 2.4 EF Core Entity Classes and Value Converters

The design doc shows `DbContext` but not the entities. Define these in `TradingEngine.Infrastructure/Persistence/Entities/`.

#### Entity conventions
- Every entity has `Id` (Guid, database primary key)
- Domain `Money` maps to two columns: `Amount` (decimal) + `Currency` (nvarchar 3)
- Domain `Price` maps to one column: decimal
- Domain `Pips` maps to one column: double
- All `DateTime` properties are stored UTC, column type `TEXT` (SQLite ISO 8601)
- Enums stored as strings (not ints) for readability and migration safety

```csharp
// TradingEngine.Infrastructure/Persistence/Entities/TradeResultEntity.cs
public sealed class TradeResultEntity
{
    public Guid Id { get; set; }
    public Guid PositionId { get; set; }
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";   // "Long" / "Short"
    public decimal Lots { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime ClosedAtUtc { get; set; }
    public decimal GrossPnLAmount { get; set; }
    public string GrossPnLCurrency { get; set; } = "";
    public decimal CommissionAmount { get; set; }
    public string CommissionCurrency { get; set; } = "";
    public decimal SwapAmount { get; set; }
    public string SwapCurrency { get; set; } = "";
    public decimal NetPnLAmount { get; set; }
    public string NetPnLCurrency { get; set; } = "";
    public double PnLPips { get; set; }
    public double RMultiple { get; set; }
    public double MaxAdverseExcursion { get; set; }
    public double MaxFavorableExcursion { get; set; }
    public string ExitReason { get; set; } = "";
    public string StrategyId { get; set; } = "";
    public string RiskProfileId { get; set; } = "";
    public string Mode { get; set; } = "";         // "Backtest" / "Paper" / "Live"
}
```

Define equivalent flat entities for `OrderEntity`, `PositionEntity`, `EquitySnapshotEntity`, `EngineEventEntity`.

#### EngineEventEntity

```csharp
public sealed class EngineEventEntity
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = "";   // nameof(TradeOpened) etc.
    public string Payload { get; set; } = "";      // JSON of the event
    public DateTime OccurredAtUtc { get; set; }
}
```

Events are serialised to JSON and stored as a string column. This is intentional — the event log is append-only audit storage, not a queryable relational table.

### 2.5 SimulatedBrokerAdapter

Used in Backtest and Paper modes. Lives in `TradingEngine.Infrastructure/Adapters/SimulatedBrokerAdapter.cs`.

**Fill model:**
- Market orders: filled on the next tick after `SubmitOrderAsync` is called
- Long market orders: filled at `tick.Ask + slippagePips * symbol.PipSize`
- Short market orders: filled at `tick.Bid - slippagePips * symbol.PipSize`
- Slippage: configurable in `SimulationOptions.SlippagePips` (default 0.5)
- Limit/stop orders: filled when price reaches the limit price (checked on each tick)
- Rejection simulation: if `SimulationOptions.RejectRate > 0`, randomly reject that fraction of orders

**SL/TP trigger model:**
- On each tick, for each open simulated position:
  - Long: if `tick.Bid <= position.CurrentStopLoss.Value` → close at SL; if `tick.Bid >= position.TakeProfit` → close at TP
  - Short: if `tick.Ask >= position.CurrentStopLoss.Value` → close at SL; if `tick.Ask <= position.TakeProfit` → close at TP
- Publish `ExecutionEvent` with `OrderState.Filled` for fills

**Channels:**
```csharp
public sealed class SimulatedBrokerAdapter : IBrokerAdapter
{
    private readonly Channel<Tick> _tickChannel = Channel.CreateUnbounded<Tick>();
    private readonly Channel<Bar> _barChannel = Channel.CreateUnbounded<Bar>();
    private readonly Channel<AccountUpdate> _accountChannel = Channel.CreateUnbounded<AccountUpdate>();
    private readonly Channel<ExecutionEvent> _executionChannel = Channel.CreateUnbounded<ExecutionEvent>();

    // Engine reads from these
    public ChannelReader<Tick> TickStream => _tickChannel.Reader;
    public ChannelReader<Bar> BarStream => _barChannel.Reader;
    public ChannelReader<AccountUpdate> AccountStream => _accountChannel.Reader;
    public ChannelReader<ExecutionEvent> ExecutionStream => _executionChannel.Reader;

    // Test harness / backtest runner writes ticks into this adapter
    public ChannelWriter<Tick> TickWriter => _tickChannel.Writer;
    public ChannelWriter<Bar> BarWriter => _barChannel.Writer;

    public DateTime BrokerTimeUtc { get; private set; } = DateTime.UtcNow;
    public bool IsConnected => true;
    // ...
}
```

### 2.6 HistoricalDataProvider and CSV Schema

Lives in `TradingEngine.Infrastructure/Adapters/HistoricalDataProvider.cs`.
Implements `IMarketDataProvider`.

**CSV file format** (`tests/data/*.csv`):

```
DateTime,Open,High,Low,Close,Volume
2024-01-02 00:00:00,1.10423,1.10456,1.10398,1.10441,1250.5
2024-01-02 01:00:00,1.10441,1.10512,1.10420,1.10489,1876.2
```

- `DateTime` column: ISO 8601 format, assumed UTC
- Prices: decimal, period as decimal separator
- Volume: double
- No header variations — this schema is fixed

**Naming convention:** `{symbol}-{timeframe}-{year}.csv` e.g. `eurusd-h1-2024.csv`
Symbol is lowercase in filename; parsed to `Symbol` value object (uppercased).
Timeframe string: `m1`, `m5`, `m15`, `m30`, `h1`, `h4`, `d1`, `w1`.

**HistoricalDataProvider implementation shape:**

```csharp
public sealed class HistoricalDataProvider(string dataDirectory) : IMarketDataProvider
{
    private DateTime _from;
    private DateTime _to;

    public Task SeekAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        _from = from;
        _to = to;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<Bar> StreamBarsAsync(
        Symbol symbol, Timeframe tf,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var path = BuildPath(symbol, tf);
        // Parse CSV, filter by _from/_to, yield each Bar
        // Synthesise Tick from each bar's OHLC using SimulatedBrokerAdapter.TickWriter
    }

    // StreamTicksAsync: synthesise 4 ticks per bar (Open, High, Low, Close)
    // using the typical spread from SymbolInfo defaults
}
```

### 2.7 Engine Loop — IHostedService Shape

Lives in `TradingEngine.Host/EngineWorker.cs`.

```csharp
public sealed class EngineWorker(
    IBrokerAdapter broker,
    IRiskManager riskManager,
    IPositionManager positionManager,
    IEnumerable<IStrategy> strategies,
    IIndicatorService indicators,
    IEventBus eventBus,
    IEngineClock clock,
    ILogger<EngineWorker> logger) : BackgroundService
{
    // Internal channel: execution events from the broker stream are re-queued here
    // and drained by the tick processor — single-threaded position state, no locks.
    private readonly Channel<ExecutionEvent> _executionEventChannel =
        Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,  // never drop execution events
            SingleWriter = true,
            SingleReader = true
        });

    // Latest AccountUpdate — swapped atomically from the account stream processor,
    // read by the tick processor on each cycle. No channel needed.
    private AccountUpdate? _latestAccountUpdate;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await broker.ConnectAsync(ct);
        await WarmUpIndicatorsAsync(ct);

        // Fan out: read tick, bar, account, execution streams concurrently
        var tasks = new[]
        {
            ProcessTicksAsync(ct),
            ProcessBarsAsync(ct),
            ProcessAccountUpdatesAsync(ct),
            ProcessExecutionEventsAsync(ct),
        };
        await Task.WhenAll(tasks);
    }

    private async Task ProcessTicksAsync(CancellationToken ct)
    {
        await foreach (var tick in broker.TickStream.ReadAllAsync(ct))
        {
            // 1. Drain pending execution events first (all that arrived since last tick)
            while (_executionEventChannel.Reader.TryRead(out var execEvent))
                HandleExecutionEvent(execEvent);

            // 2. Read latest AccountUpdate (Interlocked-swapped from account stream)
            var accountUpdate = Interlocked.Exchange(ref _latestAccountUpdate, null);
            if (accountUpdate != null)
                HandleAccountUpdate(accountUpdate.Value);

            // 3. Update equity snapshot, notify risk manager
            // 4. Evaluate each strategy → collect intents
            // 5. For each intent: validate → size → submit order
            // 6. Evaluate position manager for each open position
        }
    }

    private async Task ProcessExecutionEventsAsync(CancellationToken ct)
    {
        // Pure relay: broker execution stream → internal channel
        await foreach (var evt in broker.ExecutionStream.ReadAllAsync(ct))
            await _executionEventChannel.Writer.WriteAsync(evt, ct);
    }

    private async Task ProcessAccountUpdatesAsync(CancellationToken ct)
    {
        // Pure relay: broker account stream → Interlocked-swapped field
        await foreach (var update in broker.AccountStream.ReadAllAsync(ct))
            Interlocked.Exchange(ref _latestAccountUpdate, update);
    }

    private async Task WarmUpIndicatorsAsync(CancellationToken ct)
    {
        // Load RequiredBarCount bars from IBarRepository for each active strategy's timeframes
        // Compute initial indicator values
        // Do NOT call strategy.Evaluate() during warm-up
    }

    private void HandleExecutionEvent(ExecutionEvent evt) { /* order/position lifecycle */ }
    private void HandleAccountUpdate(AccountUpdate update) { /* equity tracking */ }
}
```

**Concurrency model (D3 — single-threaded tick processor):**
- `ProcessTicksAsync` is the **primary decision loop** — only it touches position state
- `ProcessExecutionEventsAsync` relays broker events to an internal `Channel<ExecutionEvent>` (never touches position state directly)
- `ProcessAccountUpdatesAsync` writes the latest update to an `Interlocked`-swapped field (no channel)
- At the top of each tick cycle, `ProcessTicksAsync` drains all pending execution events before evaluating strategies or positions
- No locks required. No shared mutable state between concurrent loops.

### 2.8 DI Registration in Host

`TradingEngine.Host/Program.cs`:

```csharp
var builder = Host.CreateApplicationBuilder(args);

// Mode-specific adapters
var mode = builder.Configuration.GetValue<EngineMode>("Engine:Mode");

if (mode == EngineMode.Live || mode == EngineMode.Paper)
    builder.Services.AddSingleton<IBrokerAdapter, NamedPipeBrokerAdapter>();
else
    builder.Services.AddSingleton<IBrokerAdapter, SimulatedBrokerAdapter>();

if (mode == EngineMode.Backtest)
    builder.Services.AddSingleton<IMarketDataProvider, HistoricalDataProvider>();
else
    builder.Services.AddSingleton<IMarketDataProvider, LiveMarketDataProvider>();

// Core services
builder.Services.AddSingleton<IEngineClock, BrokerClock>();
builder.Services.AddSingleton<IRiskManager, RiskManager>();
builder.Services.AddSingleton<IPositionManager, PositionManager>();
builder.Services.AddSingleton<IEventBus, TypedEventBus>();
builder.Services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
builder.Services.AddSingleton<ISlTpCalculator, SlTpCalculator>();
builder.Services.AddSingleton<ITrailingStopService, TrailingStopService>();

// Strategies — register each as IStrategy
builder.Services.AddSingleton<IStrategy, TrendBreakoutStrategy>();

// Persistence
builder.Services.AddSqliteDataProvider(
    builder.Configuration.GetConnectionString("Trading")!);

// Engine worker
builder.Services.AddHostedService<EngineWorker>();

// Options
builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection("Engine"));
builder.Services.Configure<SimulationOptions>(builder.Configuration.GetSection("Simulation"));

builder.Build().Run();
```

### 2.9 EngineTestHarness

Lives in `tests/TradingEngine.Tests.Simulation/Harness/EngineTestHarness.cs`.

```csharp
public sealed class EngineTestHarness
{
    private readonly List<IStrategy> _strategies = [];
    private RiskProfile? _riskProfile;
    private string? _dataPath;
    private PropFirmRuleSet? _propFirmRules;

    public static EngineTestHarness Create() => new();

    public EngineTestHarness WithStrategy(IStrategy strategy)
    {
        _strategies.Add(strategy);
        return this;
    }

    public EngineTestHarness WithRiskProfile(RiskProfile profile)
    {
        _riskProfile = profile;
        return this;
    }

    public EngineTestHarness WithHistoricalData(string relativePath)
    {
        _dataPath = Path.Combine("data", relativePath);
        return this;
    }

    public EngineTestHarness WithPropFirmRules(PropFirmRuleSet ruleSet)
    {
        _propFirmRules = ruleSet;
        return this;
    }

    public async Task<BacktestResult> RunBacktestAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        // Build a minimal service collection with:
        // - SimulatedBrokerAdapter
        // - HistoricalDataProvider pointing to _dataPath
        // - DataFeedService: wires HistoricalDataProvider → SimulatedBrokerAdapter writers
        // - RiskManager configured with _riskProfile and _propFirmRules
        // - StubClock starting at `from`
        // - All registered strategies
        // - SymbolInfoRegistry with defaults
        // Run the engine loop until data is exhausted
        // DataFeedService completes a TaskCompletionSource when data stream ends
        // Return BacktestResult
    }
}

public sealed record BacktestResult(
    decimal NetPnL,
    decimal MaxDrawdown,
    int TotalTrades,
    double WinRate,
    IReadOnlyList<TradeResult> Trades,
    IReadOnlyList<RiskViolation> PropFirmViolations);
```

### 2.10 Protection Mode Reset

`protectionResetPolicy: "NextTradingDay"` means:

At each daily reset time (22:00 UTC for FTMO), `RiskManager.OnDailyReset()` is called.
This method:
1. Records a new `DailyStartEquity` snapshot
2. Resets `DailyDrawdownUsed` to 0
3. If `InProtectionMode` was caused by daily DD breach only → clear protection mode
4. If `InProtectionMode` was caused by max DD breach → do NOT clear (max DD is permanent until manual override)

The daily reset is triggered by a timer in `EngineWorker`:

```csharp
// In EngineWorker, on startup, schedule daily reset
private void ScheduleDailyReset(CancellationToken ct)
{
    // Calculate next 22:00 UTC, then repeat every 24h
    // On fire: call riskManager.OnDailyReset(currentEquity)
}
```

### 2.11 Bar Warm-Up on Restart

On engine start, `WarmUpIndicatorsAsync()` loads bars from `IBarRepository` first.
If `IBarRepository` has fewer bars than `RequiredBarCount` for any strategy, request the remainder from the broker via `IBrokerAdapter` (add `Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(Symbol, Timeframe, int count, CancellationToken)` to `IBrokerAdapter`).

In `SimulatedBrokerAdapter` and `HistoricalDataProvider`, this method returns bars from the CSV data.
In `NamedPipeBrokerAdapter`, send a `"HistoricalBarsRequest"` message over the pipe and await the cBot's response.

### 2.12 Web Frontend Decision

**LOCKED:** Use ASP.NET Core Razor Pages (no JS framework). Charts rendered with Chart.js loaded via CDN. No npm, no webpack, no node.js build step.

The web viewer is an internal dev/monitoring tool — simplicity and zero build tooling outweigh UI sophistication.

Pages:
- `/` — Dashboard: current risk state (SSE-updated), today's PnL, open positions
- `/trades` — Paginated trade list with filters
- `/trades/{id}` — Trade detail with chart
- `/performance` — Performance summary (win rate, profit factor, equity curve chart)
- `/events` — Recent engine events tail

SSE endpoint: `GET /sse/risk` — streams `RiskState` JSON on each equity update.

---

## 3. Phase Breakdown

---

### Phase 1 — Solution Scaffold and Domain

**Goal:** Create the solution, all projects, and all domain types. Zero logic — only types and interfaces.

#### Files to create

```
TradingEngine.sln
Directory.Build.props
Directory.Packages.props
.editorconfig

src/TradingEngine.Domain/
  ValueObjects/
    Symbol.cs
    Money.cs
    Price.cs
    Pips.cs
    RiskPercent.cs
  MarketData/
    Tick.cs
    Bar.cs
    Timeframe.cs
    MarketContext.cs
  Trading/
    TradeDirection.cs
    OrderType.cs
    OrderState.cs
    TradeIntent.cs
    Order.cs
    Position.cs
    TradeResult.cs
    EngineMode.cs
  RiskAndEquity/
    EquitySnapshot.cs
    RiskViolation.cs
    RiskState.cs
    RiskProfile.cs
    PropFirmRuleSet.cs
  PositionManagement/
    PositionManagementConfig.cs
    TrailingConfig.cs
    TrailingMethod.cs (enum)
    PositionModification.cs
    MoveStopLoss.cs
    PartialClose.cs
    ClosePosition.cs  (position modification, not the interface)
  Events/
    EngineEvent.cs
    TradeOpened.cs
    TradeClosed.cs
    TradeBlocked.cs
    DrawdownBreached.cs
    ProtectionModeEntered.cs
    EquityUpdated.cs
  Interfaces/
    IBrokerAdapter.cs
    IStrategy.cs
    IRiskManager.cs
    IPositionManager.cs
    IDataProvider.cs
    ITradeRepository.cs
    IOrderRepository.cs
    IEquityRepository.cs
    IEventLogRepository.cs
    IBarRepository.cs
    IMarketDataProvider.cs
    IEngineClock.cs
    IEventBus.cs
    IEventHandler.cs
    IIndicatorService.cs
    ISlTpCalculator.cs
    ISymbolInfoRegistry.cs      # Get(Symbol), Register(SymbolInfo), TryGet(Symbol)
    ITrailingStopService.cs
  SymbolInfo/
    SymbolInfo.cs
    SymbolCategory.cs (enum)
```

#### Rules for this phase

- Every file contains exactly one top-level type
- All types are `public` — no `internal` in Domain
- No NuGet packages in Domain except `Microsoft.Extensions.Logging.Abstractions`
- No `class` implementations — only `record`, `record struct`, `enum`, and `interface`
- The only `class` allowed is `BrokerClock` and `StubClock` (these are Domain implementations of `IEngineClock`)
- `Position` record must NOT have `FloatingPnL()` method (see §1.1)
- `IBrokerAdapter` must include `DateTime BrokerTimeUtc { get; }` (see §1.3)

#### Validation Gate 1

```powershell
dotnet build src/TradingEngine.Domain --no-restore
# Must produce: 0 Error(s), 0 Warning(s)

# Verify no banned references:
Select-String -Path "src/TradingEngine.Domain/**/*.csproj" -Pattern "EntityFramework|Skender|Dapper|Serilog(?!.*Abstractions)"
# Must produce: no matches
```

---

### Phase 2 — Risk Engine

**Goal:** Implement all risk logic. Must be fully unit-testable with no infrastructure.

#### Files to create

```
src/TradingEngine.Risk/
  PositionSizer.cs          # static class with Calculate()
  RiskManager.cs            # implements IRiskManager
  DrawdownTracker.cs        # tracks daily + max drawdown, computes RiskState
  PropFirmRuleValidator.cs  # validates TradeIntent against PropFirmRuleSet
  NewsFilter.cs             # checks if current time is in a news window
  SessionFilter.cs          # checks if current time is within allowed sessions
  DrawdownScaler.cs         # computes drawdown scale factor from RiskProfile

config/prop-firms/
  ftmo-standard.json
  ftmo-aggressive.json      # same rules, higher daily/total loss limits

config/risk-profiles/
  conservative.json         # 0.5% risk, tight scaling
  standard.json             # 1% risk
  aggressive.json           # 2% risk
```

#### Key implementation rules

**`PositionSizer.Calculate()`** — implement exactly as design doc §6.2. No simplification. Lot rounding must use `Math.Floor`, not `Math.Round`.

**`RiskManager.Validate()`** — run all 8 checks from design doc §6.4 in that order. Return ALL violations, not just the first. This is a list accumulator pattern:

```csharp
var violations = new List<RiskViolation>();
if (CurrentState.InProtectionMode)
    violations.Add(new("PROTECTION_MODE_ACTIVE", "Trading suspended: protection mode"));
if (snapshot.CurrentDailyDrawdown >= propFirm.MaxDailyLossPercent)
    violations.Add(new("DAILY_DD_LIMIT", "Daily drawdown limit reached"));
// ... continue all 8 checks
return violations;
```

**`DrawdownTracker`** — tracks `InitialAccountBalance` (set once, never updated), `PeakEquity` (update on every positive equity move), `DailyStartEquity` (updated at daily reset), and computes both drawdown fractions on every `OnEquityUpdate()` call.

**`PropFirmRuleSet`** — deserialise from JSON. Validate on load: all percentages between 0 and 1, reset time parseable, no unknown fields (use `JsonUnmappedMemberHandling.Disallow`).

#### Validation Gate 2

```powershell
dotnet build src/TradingEngine.Risk --no-restore
# Must produce: 0 Error(s), 0 Warning(s)

dotnet test tests/TradingEngine.Tests.Unit --no-build --filter "Category=Risk"
# All risk unit tests must pass
```

**Required unit tests (write these before moving to Phase 3):**

| Test name | What it asserts |
|---|---|
| `PositionSizer_NormalCase_ReturnsCorrectLots` | EURUSD, 1% risk, $10k equity, 21 pip SL → 0.47 lots |
| `PositionSizer_WithDDScaling_ReducesLots` | Same inputs, scale 0.5 → 0.23 lots |
| `PositionSizer_AlwaysRoundsDown` | Computed 0.476 → returns 0.47, not 0.48 |
| `PositionSizer_RespectsMinLots` | Very small account → returns MinLots, not zero |
| `RiskManager_ProtectionModeActive_BlocksAllTrades` | Any intent when InProtectionMode → PROTECTION_MODE_ACTIVE violation |
| `RiskManager_DailyDDReached_BlocksTrades` | Equity at daily limit → DAILY_DD_LIMIT violation |
| `RiskManager_MaxDDReached_BlocksTrades` | Equity at max DD floor → MAX_DD_LIMIT violation |
| `RiskManager_MultipleViolations_ReturnsAll` | Two rules broken → both violations returned |
| `DrawdownTracker_InitialBalance_NeverUpdated` | Set once at activation; multiple equity updates don't change it |
| `DrawdownTracker_TrailingDD_TracksPeakEquity` | Peak rises with equity; floor rises proportionally |
| `DrawdownTracker_FixedDD_FloorIsConstant` | Peak equity increases; floor stays at initial balance floor |
| `DrawdownTracker_DailyReset_ClearsDailyDD` | After reset, daily DD is zero |
| `DrawdownTracker_DailyReset_DoesNotClearMaxDD` | Max DD persists through daily reset |
| `PropFirmRuleValidator_ProfitTarget_ChecksBalance_NotEquity` | Balance at target, equity lower → target met |

---

### Phase 3 — Infrastructure

**Goal:** EF Core persistence, SQLite provider, buffered writers, named pipe adapter.

#### Files to create

```
src/TradingEngine.Infrastructure/
  Persistence/
    Entities/
      TradeResultEntity.cs
      OrderEntity.cs
      PositionEntity.cs
      EngineEventEntity.cs
      EquitySnapshotEntity.cs
      BarEntity.cs
    Mappings/
      TradeResultMapping.cs     # IEntityTypeConfiguration<TradeResultEntity>
      (one file per entity)
    TradingDbContext.cs
    ReportingDbContext.cs
    Repositories/
      SqliteTradeRepository.cs
      SqliteOrderRepository.cs
      SqliteEquityRepository.cs
      SqliteEventLogRepository.cs
      SqliteBarRepository.cs
    SqliteDataProvider.cs
    Reporting/
      TradeReportQueries.cs     # Dapper queries
  Adapters/
    NamedPipeBrokerAdapter.cs
    SimulatedBrokerAdapter.cs
    HistoricalDataProvider.cs
    LiveMarketDataProvider.cs   # stub for now; real impl in Phase 9
  Caching/
    BufferedBarWriter.cs
    BufferedTickWriter.cs       # if needed
  Indicators/
    SkenderIndicatorService.cs  # internal sealed, implements IIndicatorService
    SkenderQuote.cs             # internal sealed, adapts Bar → IQuote
    IndicatorCache.cs           # keyed cache (symbol, tf, name, period, barCount)
  ServiceCollectionExtensions.cs  # AddSqliteDataProvider(), AddInfrastructure()
  Migrations/                   # EF Core generated — do not hand-write
```

#### Key rules

**EF Core:**
- Use `IEntityTypeConfiguration<T>` classes (one per entity) instead of `OnModelCreating` overrides
- All `decimal` columns: `.HasColumnType("TEXT")` — SQLite doesn't have a native decimal type; use TEXT with full precision. Alternatively use `decimal` with EF's default mapping, but be consistent
- All `DateTime` columns: `.HasConversion(v => v.ToString("o"), v => DateTime.Parse(v, null, DateTimeStyles.RoundtripKind))` to ensure UTC roundtrip
- `ReportingDbContext` uses `.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)` in `OnConfiguring`

**`BufferedBarWriter`:** implement exactly as design doc §7.2. `BoundedChannelFullMode.DropOldest`. Batch size 500. Never `await` a DB write from the engine tick path.

**`NamedPipeBrokerAdapter`:**
- Pipe name from config: `BrokerOptions.PipeName`
- Message framing: 4-byte little-endian int (message length in bytes) followed by UTF-8 JSON
- Reconnect: exponential backoff starting at 1s, doubling each attempt, capped at 30s
- On disconnect: push a synthetic `AccountUpdate` with a "disconnected" flag, log Critical, begin reconnect loop
- Never throw from the read loop — log and continue

**`SimulatedBrokerAdapter`:** implement per §2.5 of this guide.

**`HistoricalDataProvider`:** implement per §2.6 of this guide. CSV parsing must use `decimal.Parse` with `InvariantCulture` for prices.

#### Validation Gate 3

```powershell
dotnet build src/TradingEngine.Infrastructure --no-restore
# 0 errors, 0 warnings

dotnet test tests/TradingEngine.Tests.Integration --no-build
# All integration tests pass
```

**Required integration tests:**

| Test name | What it asserts |
|---|---|
| `SqliteTradeRepository_SaveAndRetrieve_RoundTrips` | Save TradeResult, retrieve by id → all fields match |
| `SqliteBarRepository_BulkInsert_StoresAllBars` | Enqueue 1000 bars, flush → 1000 rows in DB |
| `BufferedBarWriter_DoesNotBlockOnFull` | Channel at capacity → TryWrite returns false, does not block |
| `SqliteEventLogRepository_AppendOnly_NoUpdateMethod` | Verify via interface: no update/delete method exists |
| `HistoricalDataProvider_ParsesCsv_CorrectBarCount` | Load eurusd-h1 test fixture → correct number of bars streamed |
| `SimulatedBrokerAdapter_MarketOrder_FillsOnNextTick` | Submit long order, advance one tick → ExecutionEvent.Filled received |
| `SimulatedBrokerAdapter_LongPosition_SlTriggered_OnBidBelowSl` | Open long, push tick with Bid < SL → close event received |

---

### Phase 4 — Services Layer

**Goal:** Implement indicator adapter, SL/TP calculator, trailing stop service.

#### Files to create

```
src/TradingEngine.Services/
  SLTPCalculation/
    SlTpCalculator.cs             # implements ISlTpCalculator
    SlParameters.cs               # record: { Pips, AtrMultiplier, LookbackBars, BufferPips }
    TpParameters.cs               # record: { Pips, RRRatio, AtrMultiplier }
  TrailingStop/
    TrailingStopService.cs        # implements ITrailingStopService
  Helpers/
    PipCalculator.cs              # static: Distance, PipValuePerLot, FloatingPnL, GrossPnL
    ExcursionTracker.cs
```

#### Key rules

**`PipCalculator`** — implement all methods from domain doc §2.2, §3.2, §9. Use `decimal` throughout. The `getCrossRate` delegate must be passed in — never hardcode.

**`SlTpCalculator`** — delegate to `PipCalculator` and the static helpers from domain doc §5 and §6. The three SL methods (`FixedPips`, `AtrMultiple`, `SwingBased`) must match the domain doc implementations exactly, including `RoundToTickSize`.

**`TrailingStopService`** — implement `StepTrail`, `AtrTrail`, `Breakeven` from domain doc §8. The `BreakevenThenTrail` method applies breakeven first; once breakeven is active, switches to AtrTrail for subsequent moves.

#### Validation Gate 4

```powershell
dotnet build src/TradingEngine.Services --no-restore
# 0 errors, 0 warnings

dotnet test tests/TradingEngine.Tests.Unit --no-build --filter "Category=Services"
```

**Required unit tests:**

| Test name | What it asserts |
|---|---|
| `PipCalculator_Distance_EurUsd_CorrectPips` | |1.08420 - 1.08210| / 0.0001 = 21 pips |
| `PipCalculator_PipValue_Case1_QuoteEqualsAccount` | EURUSD USD account = $10.00 fixed |
| `PipCalculator_PipValue_Case2_BaseEqualsAccount` | USDCAD USD account = price-dependent |
| `PipCalculator_PipValue_Case3_CrossPair` | GBPJPY USD account uses getCrossRate(JPY, USD) |
| `PipCalculator_NeverUsesDouble_ForPriceArithmetic` | (code review test — assert decimal types) |
| `SlTpCalculator_FixedPip_Long_SlBelowEntry` | |
| `SlTpCalculator_FixedPip_Short_SlAboveEntry` | |
| `SlTpCalculator_AtrBased_RoundsToTickSize` | |
| `SlTpCalculator_RRMultiple_CorrectDistance` | SL 20 pips, RR 2.0 → TP 40 pips from entry |
| `TrailingStopService_StepTrail_NeverMovesBackward` | Bid drops below new trail → returns null |
| `TrailingStopService_Breakeven_OnlyFiresOnce` | Already at BE → subsequent calls return null |
| `ExcursionTracker_Mae_TracksWorstAdverse` | |
| `ExcursionTracker_Mfe_TracksBestFavorable` | |

---

### Phase 5 — Strategies

**Goal:** Implement TrendBreakoutStrategy as the validation strategy.

#### Files to create

```
src/TradingEngine.Strategies/
  TrendBreakout/
    TrendBreakoutStrategy.cs
    TrendBreakoutConfig.cs   # deserialised from trend-breakout.json

config/strategies/
  trend-breakout.json

src/TradingEngine.Strategies/
  MovingAverageTrend/
    MovingAverageTrendStrategy.cs
    MovingAverageTrendConfig.cs
  VolatilityExpansion/
    VolatilityExpansionStrategy.cs
    VolatilityExpansionConfig.cs
```

#### `TrendBreakoutStrategy` logic

```
Signal: Long when:
  - Latest bar closes above the highest high of the last N bars (configurable: lookbackBars)
  - Current price is above EMA(maPeriod)
  - ATR is above a minimum threshold (avoids trading in dead markets)

Signal: Short when:
  - Latest bar closes below the lowest low of the last N bars
  - Current price is below EMA(maPeriod)

SL: AtrBased with slAtrMultiple
TP: RRMultiple with tpRrMultiple (or null if tpRrMultiple <= 0)

Reason string: "Break of {N}-bar {high/low}, above/below EMA{period}"

RequiredBarCount: max(lookbackBars, maPeriod, atrPeriod) + 5  // buffer
```

#### Rules for all strategies

- `Evaluate()` must NEVER throw — wrap in `try/catch`, log error, return `null`
- `Evaluate()` must NEVER access `IRiskManager`, `IPositionManager`, or any infrastructure
- `Evaluate()` receives indicator values from `context.IndicatorValues` — never calls `IIndicatorService` directly
- `OnTradeResult()` may update internal state (e.g. win/loss streak) — must be thread-safe
- `Reset()` clears all internal state including streaks and last signal
- Strategies must check `context.Bars[timeframe].Count >= RequiredBarCount` at the top of `Evaluate()` and return `null` if not satisfied

#### Validation Gate 5

```powershell
dotnet build src/TradingEngine.Strategies --no-restore
# 0 errors, 0 warnings

dotnet test tests/TradingEngine.Tests.Unit --no-build --filter "Category=Strategy"
```

**Required unit tests:**

| Test name | What it asserts |
|---|---|
| `TrendBreakout_InsufficientBars_ReturnsNull` | Fewer bars than RequiredBarCount → null |
| `TrendBreakout_BreakAboveHigh_PriceAboveEma_ReturnsLongIntent` | |
| `TrendBreakout_BreakAboveHigh_PriceBelowEma_ReturnsNull` | MA filter blocks signal |
| `TrendBreakout_Evaluate_NeverThrows_OnBadInput` | Pass null indicators → no exception |
| `TrendBreakout_Reset_ClearsInternalState` | Signal after reset behaves as fresh start |
| `TrendBreakout_SL_IsOnCorrectSide` | Long intent: SL < EntryPrice |
| `TrendBreakout_TP_IsRRMultipleOfSL` | If rrRatio = 2.0, TP distance = 2 × SL distance |

---

### Phase 6 — Simulation Tests

**Goal:** End-to-end backtest through the engine with synthetic data.

#### Files to create

```
tests/TradingEngine.Tests.Simulation/
  Harness/
    EngineTestHarness.cs
    BacktestResult.cs
  Data/
    CsvDataGenerator.cs       # generates synthetic OHLCV CSV in memory for tests
  Scenarios/
    FtmoViolationScenarios.cs
    TrendBreakoutScenarios.cs
tests/data/
  eurusd-h1-sample.csv        # 3 months of real or synthetic data
```

**`CsvDataGenerator`** generates deterministic synthetic bars:
- Trending up: each bar's close slightly above previous
- Trending down: each bar's close slightly below previous
- Ranging: close oscillates in a band
- Crash scenario: equity drops 6% in one day (for DD violation tests)

#### Required simulation tests

```csharp
// Must run end-to-end through the full engine loop
[Fact] TrendBreakout_BullishData_GeneratesAtLeastOneTrade()
[Fact] TrendBreakout_BullishData_NoFtmoViolations()
[Fact] Engine_WhenDailyDDBreached_BlocksNewTrades()
[Fact] Engine_WhenMaxDDBreached_EntersProtectionMode()
[Fact] Engine_PositionManager_MovesTrailingStop()
[Fact] Engine_EventLog_HasEntryForEveryTrade()
[Fact] Engine_BacktestResult_IsReproducible()  // same inputs → same outputs
```

#### Validation Gate 6

```powershell
dotnet test tests/TradingEngine.Tests.Simulation --no-build
# All simulation tests pass

# Reproducibility check: run twice, compare trade counts
$r1 = dotnet test tests/TradingEngine.Tests.Simulation -v n 2>&1 | Select-String "passed"
$r2 = dotnet test tests/TradingEngine.Tests.Simulation -v n 2>&1 | Select-String "passed"
# Must match
```

---

### Phase 7 — Host Wiring

**Goal:** Wire everything into a runnable console host that can execute a backtest end-to-end.

#### Files to create

```
src/TradingEngine.Host/
  EngineWorker.cs             # BackgroundService (engine loop)
  DataFeedService.cs          # IHostedService: reads IMarketDataProvider, writes to SimulatedBrokerAdapter writers
  DailyResetService.cs        # BackgroundService (schedules daily reset)
  StrategyRegistry.cs         # Scans [StrategyId] attribute, resolves config to IStrategy instances
  ConfigLoader.cs             # Loads all config directories, validates cross-references
  Program.cs                  # DI registration, mode switching
  appsettings.json
  appsettings.Development.json
  appsettings.Backtest.json
```

#### `appsettings.Backtest.json`

```json
{
  "Engine": {
    "Mode": "Backtest",
    "ActiveRiskProfileId": "standard",
    "ActiveStrategyIds": ["trend-breakout"],
    "PropFirmRuleSetId": "ftmo-standard",
    "Broker": {
      "PipeName": "trading-engine",
      "ConnectionTimeoutMs": 5000,
      "ReconnectDelayMs": 1000
    }
  },
  "Simulation": {
    "SlippagePips": 0.5,
    "RejectRate": 0.0
  },
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "File", "Args": { "path": "logs/engine-.log", "rollingInterval": "Day" } }
    ]
  },
  "ConnectionStrings": {
    "Trading": "Data Source=trading-backtest.db"
  }
}
```

#### Validation Gate 7

```powershell
# Run a backtest from the command line — must complete without exception
$env:ASPNETCORE_ENVIRONMENT = "Backtest"
dotnet run --project src/TradingEngine.Host -- --mode backtest --from 2024-01-01 --to 2024-01-31
# Must exit with code 0
# Must produce structured log entries for at least: engine start, strategy registered,
# warm-up complete, at least one TradeOpened (if data has signals), engine stop
```

---

### Phase 8 — Web Viewer

**Goal:** Read-only ASP.NET Core app showing trade history, performance, and live risk state.

#### Files to create

```
src/TradingEngine.Web/
  Pages/
    Index.cshtml + .cs        # Dashboard
    Trades/Index.cshtml + .cs
    Trades/Detail.cshtml + .cs
    Performance.cshtml + .cs
    Events.cshtml + .cs
  Api/
    RiskSseController.cs      # SSE endpoint
    TradesApiController.cs    # JSON endpoints
    PerformanceApiController.cs
    ExportController.cs       # CSV export
  wwwroot/
    js/charts.js              # Chart.js chart initialisation
    css/site.css
  Program.cs
  appsettings.json
```

#### SSE implementation

```csharp
// RiskSseController.cs
[HttpGet("/sse/risk")]
public async Task StreamRisk(CancellationToken ct)
{
    Response.Headers.Append("Content-Type", "text/event-stream");
    Response.Headers.Append("Cache-Control", "no-cache");

    await foreach (var snapshot in _riskStateChannel.Reader.ReadAllAsync(ct))
    {
        var json = JsonSerializer.Serialize(snapshot);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
```

The web app subscribes to `IEventBus` on startup and pushes `EquityUpdated` events to a dedicated `Channel<RiskState>` for SSE streaming.

#### Validation Gate 8

```powershell
dotnet build src/TradingEngine.Web --no-restore
# 0 errors, 0 warnings

# Start web app pointing at a populated backtest DB
dotnet run --project src/TradingEngine.Web
# Navigate to http://localhost:5200 — all pages must load without 500 errors
# /api/trades must return JSON array
# /api/performance must return summary object
# /sse/risk must return text/event-stream content-type
```

---

### Phase 9 — cTrader Adapter

**Goal:** Thin C# 6 cBot that bridges cTrader API to the named pipe.

#### Files to create

```
src/TradingEngine.Adapters.CTrader/
  TradingEngineCBot.cs        # cBot entry point (inherits cTrader Robot class)
  PipeClient.cs               # manages named pipe connection
  PipeMessage.cs              # C# 6 POCO (no record)
  MessageSerializer.cs        # manual JSON serialisation (no System.Text.Json)
  TickPublisher.cs
  BarPublisher.cs
  AccountUpdatePublisher.cs
  OrderCommandHandler.cs
```

#### C# 6 rules (enforced by `<LangVersion>6</LangVersion>`)

- All classes, no records
- No `var` in `foreach` header if type is ambiguous — use explicit type
- No string interpolation with complex expressions (simple `$"..."` is C# 6)
- No null-conditional on method calls with args: `foo?.Bar(x)` — use null check first
- No tuple deconstruction
- JSON: use `Newtonsoft.Json` (available in cTrader environment) NOT `System.Text.Json`
- No `async`/`await` in the cBot — cTrader uses callback-based APIs

**Add to cBot csproj:**
```xml
<LangVersion>6</LangVersion>
<Nullable>disable</Nullable>
```

#### Validation Gate 9

```powershell
dotnet build src/TradingEngine.Adapters.CTrader --no-restore
# 0 errors, 0 warnings
# Specifically: no CS8xxx errors (C# 8+ features) — if any appear, the cBot will not load in cTrader
```

---

### Phase 10 — Aspire and CI/CD

**Goal:** Dev orchestration and GitHub Actions pipelines.

#### Files to create

```
aspire/TradingEngine.AppHost/
  Program.cs

.github/workflows/
  pr.yml
  release.yml
```

Aspire `Program.cs`: exactly as design doc §11.3.
CI/CD YAML: exactly as design doc §16.1 and §16.2.

#### Validation Gate 10

```powershell
dotnet build aspire/TradingEngine.AppHost --no-restore
# 0 errors

# Full test suite
dotnet test --no-build -c Release
# All tests across all three test projects pass
```

---

## 4. Architectural Validation Rules

Run these checks at any time to verify layer integrity. If any check fails, fix before continuing.

### 4.1 Domain isolation check

```powershell
# No infrastructure packages in Domain
$banned = @("EntityFramework", "Skender", "Dapper", "System.Data", "Serilog.Sinks")
$domainCsproj = Get-Content "src/TradingEngine.Domain/TradingEngine.Domain.csproj"
foreach ($pkg in $banned) {
    if ($domainCsproj -match $pkg) {
        Write-Error "VIOLATION: Domain references $pkg"
    }
}
```

### 4.2 Skender containment check

```powershell
# Skender types must not appear outside Infrastructure
$skenderRefs = Select-String -Path "src/**/*.cs" -Pattern "using Skender|Skender\." -Recurse
$violations = $skenderRefs | Where-Object { $_.Filename -notmatch "Infrastructure" }
if ($violations) { Write-Error "VIOLATION: Skender used outside Infrastructure" }
```

### 4.3 DateTime.Now / DateTime.UtcNow check

```powershell
$dtNow = Select-String -Path "src/**/*.cs" -Pattern "DateTime\.Now|DateTime\.UtcNow" -Recurse
$allowed = $dtNow | Where-Object { $_.Filename -match "BrokerClock|StubClock" }
$violations = $dtNow | Where-Object { $_.Filename -notmatch "BrokerClock|StubClock" }
if ($violations) { Write-Error "VIOLATION: DateTime.Now/UtcNow used outside clock implementations" }
```

### 4.4 Double for money check

```powershell
# Flag any 'double' variable named with money-adjacent names
$moneyDouble = Select-String -Path "src/**/*.cs" `
    -Pattern "double\s+(price|equity|balance|pnl|lots|risk|amount|money)" `
    -Recurse -CaseSensitive
if ($moneyDouble) { Write-Warning "REVIEW: double used for money-adjacent variable" }
```

### 4.5 Async void check

```powershell
$asyncVoid = Select-String -Path "src/**/*.cs" -Pattern "async void" -Recurse
if ($asyncVoid) { Write-Error "VIOLATION: async void found — use async Task or IHostedService" }
```

### 4.6 Full dependency graph check

```powershell
# Verify project references match the allowed dependency graph
# Domain: no ProjectReferences
$domainRefs = Select-String -Path "src/TradingEngine.Domain/**/*.csproj" -Pattern "<ProjectReference"
if ($domainRefs) { Write-Error "VIOLATION: Domain has project references" }

# Risk: only Domain and Application
# Strategies: only Domain, Application, Services
# (Extend this pattern for each project)
```

### 4.7 cBot C# version check

```powershell
$cbot = Get-Content "src/TradingEngine.Adapters.CTrader/TradingEngine.Adapters.CTrader.csproj"
if ($cbot -notmatch "<LangVersion>6</LangVersion>") {
    Write-Error "VIOLATION: cBot project must have <LangVersion>6</LangVersion>"
}
```

---

## 5. Cross-Cutting Rules (consolidated)

These apply across all phases. Check before every file you write.

### Financial arithmetic
- `decimal` for: prices, pip sizes, lot sizes, money amounts, equity, balance, drawdown thresholds
- `double` for: indicator values (ATR, EMA, RSI output), statistical metrics (win rate, R-multiple), MAE/MFE in pips when used for display
- Never use `float` anywhere
- When casting between `decimal` and `double` for indicator boundaries, add a comment explaining why

### Null discipline
- No nullable reference types in domain — use `required` and validate at boundaries
- All Skender results are nullable — use `?.Property ?? defaultValue` always
- `TradeIntent.TakeProfit` is `Price?` — null means trail to exit, not a bug
- `Price?` null from SL/TP calculator means "not applicable" — propagate null, do not substitute zero

### Concurrency
- All `Channel` consumers use `ReadAllAsync(ct)` in `await foreach`
- Never `channel.Reader.TryRead()` in a spin loop
- `IPositionManager` state is accessed from the tick loop only — no concurrent position mutations
- `IStrategy.Evaluate()` is called from the tick loop — strategies must not share mutable state across symbols

### Error handling
- `IStrategy.Evaluate()`: catch all exceptions, log as Error with strategy ID and context, return null
- `IBrokerAdapter` read loops: catch all exceptions, log as Error, attempt reconnect
- `IRiskManager.Validate()`: never throw — validation failures are returned as `RiskViolation` list
- `IRiskManager.CalculateLotSize()`: throw `RiskViolationException` if lot size cannot be computed (zero SL distance, zero pip value, etc.)

### Logging
- Every trade open: `LogInformation` with TradeId, Symbol, Direction, Lots, EntryPrice, SL, TP, StrategyId
- Every trade close: `LogInformation` with TradeId, ExitPrice, NetPnL, RMultiple, ExitReason
- Every blocked intent: `LogWarning` with StrategyId, Symbol, Violations
- Every protection mode entry: `LogCritical` with reason and trigger equity
- No log statements inside `IStrategy.Evaluate()` except on error — the tick loop runs at high frequency

### Configuration
- Unknown JSON keys throw on deserialisation (`JsonUnmappedMemberHandling.Disallow`)
- Missing required keys throw on startup validation — no silent defaults
- All config loaded and validated in `Program.cs` before any service is started
- Strategy configs reference `riskProfileId` which must exist in `config/risk-profiles/` — validate cross-references on startup

### Testing
- Test method names: `MethodName_Condition_ExpectedResult`
- Use `StubClock` for all time-sensitive tests — never `DateTime.UtcNow` in test setup
- Use `NSubstitute` for mocking interfaces — never hand-roll mocks
- Use `FluentAssertions` for all assertions — never `Assert.Equal` directly
- Test data CSV files committed to `tests/data/` — never download at test runtime
- Integration tests use SQLite `:memory:` — never write to a file DB in tests

---

## 6. Things DeepSeek / the Agent Must Never Do

These are the most common model mistakes on this codebase:

1. **Do not add `async` to `IStrategy.Evaluate()`** — strategies are synchronous. Making them async breaks the tick loop contract.

2. **Do not use `Newtonsoft.Json` in any project except the cBot** — all other projects use `System.Text.Json`.

3. **Do not add a `FloatingPnL()` method to the `Position` record** — that method is in `PipCalculator` and requires `SymbolInfo`. See §1.1.

4. **Do not make `PositionSizer.Calculate()` a member method** — it is a `public static` method on a `public static class PositionSizer`. This allows unit testing without DI.

5. **Do not use `var` for `Channel` declarations** — always write the full type so the reader can see `BoundedChannelFullMode` explicitly.

6. **Do not add navigation properties to EF Core entities** — no `.Include()` chains. All reads are flat; joins are in Dapper SQL.

7. **Do not call `IIndicatorService` from inside a strategy** — strategies read from `context.IndicatorValues`. The engine loop populates that dictionary before calling `Evaluate()`.

8. **Do not create a `BaseStrategy` abstract class** — strategies implement `IStrategy` directly. There is no shared base. If two strategies share logic, extract it to a static helper in `TradingEngine.Services`.

9. **Do not handle DST in UTC arithmetic** — always use `TimeZoneInfo.ConvertTimeFromUtc()` for session and reset time checks. Never add/subtract hours manually for timezone offsets.

10. **Do not reference `TradingEngine.AppHost` from `Host` or `Web`** — Aspire orchestrator is dev-only. Production must start `Host` and `Web` independently.

11. **Do not add Swagger/OpenAPI to `TradingEngine.Web`** — it is an internal dev tool, not a public API. No Swagger middleware.

12. **Do not add an `Update()` or `Delete()` method to `IEventLogRepository`** — it is append-only by design. If you think you need it, you are solving the wrong problem.

13. **Do not use `Math.Round()` for lot sizes** — always `Math.Floor(lots / lotStep) * lotStep`. See domain doc §7.1 and §13, mistake #3.

14. **Do not hardcode currency pairs in pip value calculation** — always use `getCrossRate(quoteCurrency, accountCurrency)`. Never write `if (symbol == "GBPJPY")`.

---

## 7. Definition of Done — Full Project

The implementation is complete when:

- [ ] `dotnet build` passes with 0 errors, 0 warnings on all projects
- [ ] `dotnet test` passes on all three test projects (Unit, Integration, Simulation)
- [ ] Architectural validation scripts in §4 all pass with 0 violations
- [ ] `dotnet run --project src/TradingEngine.Host` in Backtest mode completes a 3-month backtest and writes results to SQLite
- [ ] `dotnet run --project src/TradingEngine.Web` starts and all pages render without errors against the backtest DB
- [ ] No `DateTime.Now` or `DateTime.UtcNow` outside `BrokerClock.cs` and `StubClock.cs`
- [ ] No Skender type outside `TradingEngine.Infrastructure`
- [ ] cBot project compiles with `<LangVersion>6</LangVersion>` and no C# 7+ features
- [ ] All risk violation codes (`PROTECTION_MODE_ACTIVE`, `DAILY_DD_LIMIT`, `MAX_DD_LIMIT`, etc.) have a unit test
- [ ] FTMO simulation test: 3-month backtest on `eurusd-h1-sample.csv` produces zero `PropFirmViolations`
- [ ] Event log test: every `TradeOpened` event has a corresponding `TradeClosed` event in the log

---

*This guide is a companion to `trading-engine-design-v1.md` and `trading-domain-knowledge.md`.
All three documents must be provided to the agent at the start of each coding session.*
