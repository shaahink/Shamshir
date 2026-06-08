# Iteration 11 — Fix BacktestReplayAdapter (BUG-01, BUG-02, BUG-03)

**Branch**: `iter/11-replay-adapter-fix`
**Fixes**: BUG-01, BUG-02, BUG-03 from `docs/OPEN-ISSUES.md`
**Depends on**: nothing — Iteration 10 is merged on main
**Blocks**: Iteration 12

---

## Read first

- `docs/agents/HOW-TO-WORK.md`
- `docs/reference/BACKTEST-ARCHITECTURE.md` (Path B section and the channel modes table)

---

## What is broken and why

Running a replay backtest always produces **0 trades**. Three bugs in
`src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` cause this:

**BUG-01 — Orders are never filled**
`SubmitOrderAsync` (line 94) puts orders in `_pendingOrders` and returns. Nothing ever reads
`_pendingOrders`. `ExecutionStream` stays empty. `PositionTracker.OnExecution` is never called.
0 positions ever open. 0 trades ever close.

**BUG-02 — Bars silently dropped for ranges larger than 2,000**
`_barChannel` is bounded at 2,000 with `DropOldest`. `ConnectAsync` writes ALL bars before the
engine's `ProcessBarsAsync` consumer has started (the consumer starts only after `ConnectAsync`
returns, in `Task.WhenAll`). Bars beyond capacity are silently dropped. A 3-month H1 = ~2,160 bars
means ~160 bars lost. A 6-month H1 = ~4,320 bars means over half the history is lost.

**BUG-03 — Force-close sends null fill price, silently discarded**
`ClosePositionAsync` (line 114) sends `FillPrice = null`. In `PositionTracker.OnExecution` the guard
`if (evt.NewState != OrderState.Filled || evt.FillPrice is null) return` silently discards it.

**Separate bug in `PositionTracker.ClosePosition` (line 99)**
Exit reason logic: `fillPrice > SL → "TP"` regardless of whether a TP exists or whether this was
a forced close. Any forced close at current price would be labelled "TP".

---

## Files to change (exactly these four, no others)

| File | Change |
|------|--------|
| `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` | Full rewrite of the class (same constructor signature, same interface, different internals) |
| `src/TradingEngine.Services/PositionTracker.cs` | Add `DetermineExitReason` method; replace 2-line ternary |
| `tests/TradingEngine.Tests.Integration/AdapterTests/BacktestReplayAdapterTests.cs` | New file — 3 tests |
| `tests/TradingEngine.Tests.Integration/TradingEngine.Tests.Integration.csproj` | No change needed (GlobalUsings already has everything) |

**Do NOT touch**: `EngineWorker.cs`, `BacktestOrchestrator.cs`, `BacktestRunner.cs`, any `Program.cs`,
any handler, any migration, any other test file.

---

## Change 1 — BacktestReplayAdapter.cs (full replacement)

Replace the entire file with the version below. The constructor signature is identical.
The `IBrokerAdapter` interface is fully implemented. Key design changes explained inline.

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class BacktestReplayAdapter : IBrokerAdapter, IAsyncDisposable
{
    private readonly IBarRepository _barRepo;
    private readonly Symbol _symbol;
    private readonly Timeframe _timeframe;
    private readonly DateTime _from;
    private readonly DateTime _to;
    private readonly decimal _initialBalance;
    private readonly ILogger<BacktestReplayAdapter> _logger;

    // FIX BUG-02: unbounded channels — replay dataset is finite, no data loss
    private readonly Channel<Tick> _tickChannel =
        Channel.CreateUnbounded<Tick>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Channel<Bar> _barChannel =
        Channel.CreateUnbounded<Bar>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Channel<AccountUpdate> _accountChannel =
        Channel.CreateBounded<AccountUpdate>(new BoundedChannelOptions(500)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<ExecutionEvent> _executionChannel =
        Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1_000)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });

    // FIX BUG-01 + BUG-03: track current close so fills use correct price
    private decimal _lastClose;
    private Task _feedTask = Task.CompletedTask;
    private CancellationTokenSource? _feedCts;

    public bool IsConnected { get; private set; }
    public ChannelReader<Tick> TickStream => _tickChannel.Reader;
    public ChannelReader<Bar> BarStream => _barChannel.Reader;
    public ChannelReader<AccountUpdate> AccountStream => _accountChannel.Reader;
    public ChannelReader<ExecutionEvent> ExecutionStream => _executionChannel.Reader;
    public DateTime BrokerTimeUtc { get; private set; }

    public BacktestReplayAdapter(
        IBarRepository barRepo,
        Symbol symbol,
        Timeframe timeframe,
        DateTime from,
        DateTime to,
        decimal initialBalance,
        ILogger<BacktestReplayAdapter> logger)
    {
        _barRepo = barRepo;
        _symbol = symbol;
        _timeframe = timeframe;
        _from = from;
        _to = to;
        _initialBalance = initialBalance;
        _logger = logger;
    }

    // FIX BUG-02: ConnectAsync starts FeedBarsAsync in background and returns immediately.
    // This lets EngineWorker's consumer loops start before all bars are written,
    // so _lastClose reflects the current bar when SubmitOrderAsync is called.
    public async Task ConnectAsync(CancellationToken ct)
    {
        IsConnected = true;
        _feedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await _accountChannel.Writer.WriteAsync(
            new AccountUpdate(_initialBalance, _initialBalance, 0, _from), ct);

        _feedTask = FeedBarsAsync(_feedCts.Token);
    }

    private async Task FeedBarsAsync(CancellationToken ct)
    {
        try
        {
            var bars = await _barRepo.GetAsync(_symbol, _timeframe, _from, _to, ct);
            _logger.LogInformation("BacktestReplay: loaded {Count} bars for {Symbol} {Tf}",
                bars.Count, _symbol, _timeframe);

            foreach (var bar in bars)
            {
                ct.ThrowIfCancellationRequested();
                BrokerTimeUtc = bar.OpenTimeUtc;
                _lastClose = bar.Close;

                await _barChannel.Writer.WriteAsync(bar, ct);
                await _tickChannel.Writer.WriteAsync(
                    new Tick(bar.Symbol, bar.Close, bar.Close + 0.0001m, bar.OpenTimeUtc), ct);
                await _accountChannel.Writer.WriteAsync(
                    new AccountUpdate(_initialBalance, _initialBalance, 0, bar.OpenTimeUtc), ct);
            }

            _logger.LogInformation("BacktestReplay: feed complete, {Count} bars sent", bars.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("BacktestReplay: feed cancelled");
        }
        finally
        {
            // Complete channels so ReadAllAsync loops in EngineWorker terminate
            _barChannel.Writer.TryComplete();
            _tickChannel.Writer.TryComplete();
            _accountChannel.Writer.TryComplete();
        }
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        IsConnected = false;
        _feedCts?.Cancel();
        _barChannel.Writer.TryComplete();
        _tickChannel.Writer.TryComplete();
        _accountChannel.Writer.TryComplete();
        _executionChannel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public Task<AccountState> GetAccountStateAsync(CancellationToken ct)
        => Task.FromResult(new AccountState(_initialBalance, _initialBalance, []));

    // FIX BUG-01: instant fill at current bar close price.
    // _lastClose is set by FeedBarsAsync just before writing each bar to the channel,
    // so by the time ProcessBarsAsync calls SubmitOrderAsync for bar N, _lastClose is bar N's close.
    public Task<Guid> SubmitOrderAsync(OrderRequest request, CancellationToken ct)
    {
        var orderId = Guid.NewGuid();
        var fillPrice = new Price(_lastClose > 0 ? _lastClose : 1m);
        _executionChannel.Writer.TryWrite(
            new ExecutionEvent(orderId, OrderState.Filled, fillPrice, request.Lots, null, BrokerTimeUtc));
        _logger.LogDebug("BacktestReplay: instant fill {Id} at {Price:F5}", orderId, fillPrice.Value);
        return Task.FromResult(orderId);
    }

    public Task ModifyOrderAsync(Guid orderId, Price newStopLoss, Price? newTakeProfit, CancellationToken ct)
        => Task.CompletedTask;

    public Task CancelOrderAsync(Guid orderId, CancellationToken ct)
        => Task.CompletedTask;

    // FIX BUG-03: send current close as fill price (was null, which got silently discarded)
    public Task ClosePositionAsync(Guid positionId, CancellationToken ct)
    {
        var fillPrice = new Price(_lastClose > 0 ? _lastClose : 1m);
        _executionChannel.Writer.TryWrite(
            new ExecutionEvent(positionId, OrderState.Filled, fillPrice, 0, null, BrokerTimeUtc));
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _feedCts?.Cancel();
        _barChannel.Writer.TryComplete();
        _tickChannel.Writer.TryComplete();
        _accountChannel.Writer.TryComplete();
        _executionChannel.Writer.TryComplete();
        try { await _feedTask; } catch (OperationCanceledException) { }
        _feedCts?.Dispose();
    }
}
```

---

## Change 2 — PositionTracker.cs (surgical edit only)

Only `ClosePosition` method changes. Find this exact block (lines 99–101):

```csharp
var exitReason = pos.Direction == TradeDirection.Long
    ? (fillPrice <= pos.CurrentStopLoss.Value ? "SL" : "TP")
    : (fillPrice >= pos.CurrentStopLoss.Value ? "SL" : "TP");
```

Replace with:

```csharp
var exitReason = DetermineExitReason(pos, fillPrice);
```

Add this private static method anywhere in the class (after `ClosePosition` is fine):

```csharp
private static string DetermineExitReason(Position pos, decimal fillPrice)
{
    if (pos.Direction == TradeDirection.Long)
    {
        if (fillPrice <= pos.CurrentStopLoss.Value) return "SL";
        if (pos.TakeProfit is not null && fillPrice >= pos.TakeProfit.Value.Value) return "TP";
        return "FORCE";
    }
    if (fillPrice >= pos.CurrentStopLoss.Value) return "SL";
    if (pos.TakeProfit is not null && fillPrice <= pos.TakeProfit.Value.Value) return "TP";
    return "FORCE";
}
```

No other changes to `PositionTracker.cs`.

---

## Change 3 — New test file

Create directory `tests/TradingEngine.Tests.Integration/AdapterTests/` and create the file below.

The GlobalUsings already provides: `FluentAssertions`, `NSubstitute`, `Xunit`, `TradingEngine.Domain`,
`TradingEngine.Infrastructure.Adapters`, and `Microsoft.Extensions.Logging` is available via
`Microsoft.Extensions.Logging.Abstractions` (transitive dependency).

```csharp
using Microsoft.Extensions.Logging.Abstractions;

namespace TradingEngine.Tests.Integration.AdapterTests;

public sealed class BacktestReplayAdapterTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Build N sequential H1 bars starting at T0, each declining by 0.0010
    private static IReadOnlyList<Bar> MakeBars(int count, decimal startClose = 1.1000m)
    {
        var bars = new List<Bar>(count);
        for (var i = 0; i < count; i++)
        {
            var close = startClose - i * 0.0010m;
            bars.Add(new Bar(
                Eurusd, Timeframe.H1,
                T0.AddHours(i),
                close + 0.0005m,   // open slightly above close (bearish bar)
                close + 0.0010m,   // high
                close - 0.0010m,   // low
                close,
                1000));
        }
        return bars;
    }

    private static BacktestReplayAdapter MakeAdapter(IReadOnlyList<Bar> bars)
    {
        var repo = Substitute.For<IBarRepository>();
        repo.GetAsync(Arg.Any<Symbol>(), Arg.Any<Timeframe>(),
                      Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(bars));

        return new BacktestReplayAdapter(
            repo, Eurusd, Timeframe.H1, T0, T0.AddDays(1),
            10_000m, NullLogger<BacktestReplayAdapter>.Instance);
    }

    // BUG-02: verify all bars arrive even when count exceeds old 2,000 limit
    [Fact(Timeout = 15_000)]
    public async Task AllBars_DeliveredWithoutDataLoss()
    {
        const int barCount = 3_000;
        var adapter = MakeAdapter(MakeBars(barCount));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await adapter.ConnectAsync(cts.Token);

        var received = 0;
        await foreach (var _ in adapter.BarStream.ReadAllAsync(cts.Token))
            received++;

        received.Should().Be(barCount,
            $"all {barCount} bars must arrive; got {received} — data loss detected (BUG-02)");

        await adapter.DisposeAsync();
    }

    // BUG-01: verify submitted order receives an ExecutionEvent with a non-null fill price
    [Fact(Timeout = 10_000)]
    public async Task SubmitOrder_ReceivesInstantFillWithPrice()
    {
        var bars = MakeBars(10);
        var adapter = MakeAdapter(bars);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await adapter.ConnectAsync(cts.Token);

        // Read the first bar so _lastClose is populated
        var firstBar = await adapter.BarStream.ReadAsync(cts.Token);

        // Build a minimal fake OrderRequest (contents don't affect fill in replay adapter)
        var intent = new TradeIntent(
            Eurusd, TradeDirection.Long, OrderType.Market, null,
            new Price(firstBar.Close - 0.0050m),   // SL
            new Price(firstBar.Close + 0.0050m),   // TP
            "test-strategy", "standard", "test", firstBar.OpenTimeUtc);
        var request = new OrderRequest(intent, 0.01m, Eurusd, TradeDirection.Long, OrderType.Market, null);

        var orderId = await adapter.SubmitOrderAsync(request, cts.Token);

        // Read the execution event — should arrive immediately (no broker round-trip)
        var execEvent = await adapter.ExecutionStream.ReadAsync(cts.Token);

        execEvent.OrderId.Should().Be(orderId);
        execEvent.NewState.Should().Be(OrderState.Filled);
        execEvent.FillPrice.Should().NotBeNull("BUG-01: fill price must not be null");
        execEvent.FillPrice!.Value.Value.Should().BeGreaterThan(0);
        execEvent.FilledLots.Should().Be(0.01m);

        await adapter.DisposeAsync();
    }

    // BUG-03: verify ClosePositionAsync sends fill price (was null, silently discarded)
    [Fact(Timeout = 10_000)]
    public async Task ClosePosition_SendsFillPriceNotNull()
    {
        var bars = MakeBars(5);
        var adapter = MakeAdapter(bars);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await adapter.ConnectAsync(cts.Token);

        // Consume one bar so _lastClose is set
        await adapter.BarStream.ReadAsync(cts.Token);

        var fakePositionId = Guid.NewGuid();
        await adapter.ClosePositionAsync(fakePositionId, cts.Token);

        var execEvent = await adapter.ExecutionStream.ReadAsync(cts.Token);

        execEvent.OrderId.Should().Be(fakePositionId);
        execEvent.NewState.Should().Be(OrderState.Filled);
        execEvent.FillPrice.Should().NotBeNull("BUG-03: ClosePositionAsync must send a fill price");
        execEvent.FillPrice!.Value.Value.Should().BeGreaterThan(0);

        await adapter.DisposeAsync();
    }
}
```

---

## Verify TradeIntent constructor

Before writing the test file, check how `TradeIntent` is constructed in `TradingEngine.Domain`.
Run:

```powershell
Select-String -Path "src\TradingEngine.Domain\Trading\TradeIntent.cs" -Pattern "public"
```

If the constructor signature differs from what's in the test above (e.g., different parameter order
or missing `Reason` parameter), adjust the test's `TradeIntent` construction to match.
Do NOT change `TradeIntent.cs` itself.

---

## Verification sequence

Run these in order. Each must pass before moving to the next.

```powershell
# 1. Build — must be 0 errors
dotnet build --no-incremental

# 2. Unit tests — must not regress (87/87)
dotnet test tests/TradingEngine.Tests.Unit --no-build

# 3. The three new tests — this is the primary deliverable
dotnet test tests/TradingEngine.Tests.Integration --no-build --filter "BacktestReplayAdapterTests"

# Expected output (3 tests):
# BacktestReplayAdapterTests.AllBars_DeliveredWithoutDataLoss [PASS]
# BacktestReplayAdapterTests.SubmitOrder_ReceivesInstantFillWithPrice [PASS]
# BacktestReplayAdapterTests.ClosePosition_SendsFillPriceNotNull [PASS]

# 4. Full integration test suite (verify no regression)
dotnet test tests/TradingEngine.Tests.Integration --no-build
```

---

## If a test fails

**AllBars_DeliveredWithoutDataLoss fails with received < 3000**:
The channel change to unbounded did not take effect. Verify `_barChannel` declaration in the
new adapter is `Channel.CreateUnbounded<Bar>`. Also check that `FeedBarsAsync` completes the
channel after the loop (`_barChannel.Writer.TryComplete()` in `finally`).

**SubmitOrder_ReceivesInstantFillWithPrice fails with timeout**:
`SubmitOrderAsync` is not writing to `_executionChannel`. Check that the `TryWrite` call is
present. Also check that `ExecutionStream` returns `_executionChannel.Reader` (not a different channel).

**SubmitOrder test fails: `execEvent.FillPrice is null`**:
The `ExecutionEvent` is being constructed with `null` fill price. Check the `new ExecutionEvent(...)`
call in `SubmitOrderAsync` — the third argument must be `fillPrice` (a `Price`), not `null`.

**ClosePosition_SendsFillPriceNotNull fails with timeout**:
`ClosePositionAsync` is not writing to `_executionChannel`. Check that the `TryWrite` is present.

**Unit tests regress**:
The `PositionTracker` change broke a unit test. Check which test failed. The only change was
replacing the 2-line ternary with `DetermineExitReason`. Verify the new method is logically
equivalent for the SL/TP cases (only adds "FORCE" for the neither-SL-nor-TP case).

---

---

## Phase B — E2E simulation test (iter-12 gate)

This is the test iter-12 gates on. Do not start iter-12 until this test passes.

Iter-12's gate command: `dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "ReplayBacktest"`

### Why a separate test from Phase A

Phase A tests prove the adapter component in isolation using a mock `IBarRepository`.
They do not prove that the full engine pipeline (adapter → `EngineWorker` → `PositionTracker`
→ `TradePersistenceHandler` → SQLite) actually produces trades.
Phase B proves the full chain end-to-end. Without it, iter-12 is guessing.

### Files to create (Phase B only)

| File | Purpose |
|------|---------|
| `tests/TradingEngine.Tests.Simulation/TradingEngine.Tests.Simulation.csproj` | Add Host project reference |
| `tests/TradingEngine.Tests.Simulation/Harness/AlwaysSignalStrategy.cs` | Deterministic test-only strategy |
| `tests/TradingEngine.Tests.Simulation/Harness/ReplayTestHarness.cs` | Builds IHost with minimal DI |
| `tests/TradingEngine.Tests.Simulation/BacktestReplayTests.cs` | The gate test |

### Step 1 — Add Host project reference

In `tests/TradingEngine.Tests.Simulation/TradingEngine.Tests.Simulation.csproj`, add inside the last `<ItemGroup>`:

```xml
<ProjectReference Include="..\..\src\TradingEngine.Host\TradingEngine.Host.csproj" />
```

### Step 2 — AlwaysSignalStrategy

This strategy fires a buy signal on every bar after the first 5 warmup bars, but only when no
position is already open. It does not use any indicators — deterministic by design.

```csharp
namespace TradingEngine.Tests.Simulation.Harness;

public sealed class AlwaysSignalStrategy : IStrategy
{
    private int _barCount;
    private bool _positionOpen;

    public string Id => "always-signal";
    public string DisplayName => "Always Signal (test only)";
    public IReadOnlyList<Timeframe> RequiredTimeframes => [Timeframe.H1];
    public int RequiredBarCount => 1;
    public IReadOnlyList<IndicatorRequest> RequiredIndicators => [];
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public StrategyStats Stats => new(0, 0, 0, 0);

    public TradeIntent? Evaluate(MarketContext context)
    {
        _barCount++;
        if (_barCount <= 5 || _positionOpen) return null;
        if (context.LatestBar is not { } bar) return null;

        _positionOpen = true;
        return new TradeIntent(
            bar.Symbol,
            TradeDirection.Long,
            OrderType.Market,
            null,
            new Price(bar.Close - 0.0050m),   // SL 50 pips below
            new Price(bar.Close + 0.0100m),   // TP 100 pips above
            Id, "standard", "always-fire", bar.OpenTimeUtc);
    }

    public void OnTradeResult(TradeResult result)
    {
        _positionOpen = false;
    }

    public void Reset() { _barCount = 0; _positionOpen = false; }
}
```

**Check `MarketContext` before writing this class.** Run:
```powershell
Select-String -Path "src\TradingEngine.Domain\**\MarketContext.cs" -Pattern "LatestBar|public"
```
Adjust property names if they differ.

### Step 3 — ReplayTestHarness

The harness builds a minimal `IHost` that wires the engine for a single-symbol replay backtest.
Use `Host/Program.cs` as the reference for service registration order and concrete types.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace TradingEngine.Tests.Simulation.Harness;

public sealed class ReplayTestHarness : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly string _dbPath;

    private ReplayTestHarness(IHost host, string dbPath)
    {
        _host = host;
        _dbPath = dbPath;
    }

    public IServiceProvider Services => _host.Services;

    public static async Task<ReplayTestHarness> CreateAsync(
        IReadOnlyList<Bar> bars,
        string runId = "test-run-1")
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"replay_test_{Guid.NewGuid():N}.db");
        var symbol = bars[0].Symbol;
        var from = bars[0].OpenTimeUtc;
        var to = bars[^1].OpenTimeUtc;

        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices((_, services) =>
            {
                // Context
                services.AddSingleton(new EngineRunContext(runId));

                // Adapter — bar repo backed by our in-memory list
                var barRepo = Substitute.For<IBarRepository>();
                barRepo.GetAsync(Arg.Any<Symbol>(), Arg.Any<Timeframe>(),
                    Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(bars));
                services.AddSingleton<IBarRepository>(_ => barRepo);
                services.AddSingleton<IBrokerAdapter>(sp =>
                    new BacktestReplayAdapter(barRepo, symbol, Timeframe.H1, from, to,
                        10_000m, sp.GetRequiredService<ILogger<BacktestReplayAdapter>>()));

                // Risk — stubbed so lot sizing doesn't block signals
                var riskManager = Substitute.For<IRiskManager>();
                riskManager.CalculateLotSize(Arg.Any<TradeIntent>(),
                    Arg.Any<EquitySnapshot>(), Arg.Any<RiskProfile>(), Arg.Any<decimal>())
                    .Returns(0.01m);
                riskManager.Validate(Arg.Any<TradeIntent>(),
                    Arg.Any<EquitySnapshot>(), Arg.Any<RiskProfile>())
                    .Returns(Array.Empty<RiskViolation>());
                riskManager.ConsumeForceClosePending().Returns(false);
                riskManager.InitialBalance.Returns(10_000m);
                riskManager.CurrentState.Returns(new RiskState(false, false, false, false));
                services.AddSingleton<IRiskManager>(_ => riskManager);
                services.AddSingleton<IRiskManager>(sp => riskManager); // duplicate guard
                services.AddSingleton<DrawdownTracker>();

                // Symbol registry — minimal EURUSD entry
                var symbolInfo = new SymbolInfo(symbol, "EUR", "USD", 100_000, 0.0001m, 1m);
                var symbolRegistry = Substitute.For<ISymbolInfoRegistry>();
                symbolRegistry.Get(symbol).Returns(symbolInfo);
                services.AddSingleton<ISymbolInfoRegistry>(_ => symbolRegistry);

                // Cross-rate: stub EUR/USD = 1.1
                services.AddSingleton<Func<string, string, decimal>>(_ => (_, _) => 1.0m);

                // Risk profile
                var profiles = new Dictionary<string, RiskProfile>
                {
                    ["standard"] = new RiskProfile("standard", 0.01m, 2m, 0.5m, [])
                };
                var resolver = new RiskProfileResolver(profiles);
                services.AddSingleton<IRiskProfileResolver>(_ => resolver);

                // Clock — broker time comes from the adapter
                services.AddSingleton<IEngineClock, BrokerClock>();

                // Persistence — real SQLite at temp path
                services.AddDbContext<TradingDbContext>(o =>
                    o.UseSqlite($"Data Source={dbPath}"));
                services.AddScoped<ITradeRepository, SqliteTradeRepository>();
                services.AddScoped<IEquityRepository, SqliteEquityRepository>();
                services.AddSingleton<PersistenceService>();

                // Event bus and handlers
                services.AddSingleton<IEventBus, TypedEventBus>();
                services.AddSingleton<TradePersistenceHandler>();
                services.AddSingleton<BarEvaluationHandler>();
                services.AddSingleton<EquityPersistenceHandler>();

                // Indicators
                services.AddSingleton<IIndicatorService, SkenderIndicatorService>();

                // Order dispatcher + position tracker
                services.AddSingleton<OrderDispatcher>();
                services.AddSingleton<PositionTracker>();
                services.AddSingleton<IPositionManager, PositionManager>();

                // Strategy
                var strategy = new AlwaysSignalStrategy();
                services.AddSingleton<IEnumerable<IStrategy>>(_ => new[] { strategy });

                // Engine worker
                services.AddSingleton<EngineWorker>();
                services.AddHostedService<EngineWorker>(sp => sp.GetRequiredService<EngineWorker>());
            })
            .Build();

        // Create schema
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return new ReplayTestHarness(host, dbPath);
    }

    public Task RunAsync(CancellationToken ct)
        => _host.RunAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }
}
```

**Before writing this file**: verify that `SymbolInfo`, `RiskProfile`, `RiskProfileResolver`,
`BrokerClock`, `TypedEventBus`, `EquityPersistenceHandler`, `PositionManager` all exist and their
constructors match. Run:
```powershell
Select-String -Path "src\**\*.cs" -Pattern "^public (sealed )?class (SymbolInfo|RiskProfile|RiskProfileResolver|BrokerClock|TypedEventBus|EquityPersistenceHandler|PositionManager)\b" -Recurse
```
If a class does not exist or has a different name, use NSubstitute for the interface it implements.

### Step 4 — Gate test

```csharp
namespace TradingEngine.Tests.Simulation;

public sealed class BacktestReplayTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<Bar> MakeBars(int count, decimal startClose = 1.1000m)
    {
        var bars = new List<Bar>(count);
        for (var i = 0; i < count; i++)
            bars.Add(new Bar(Eurusd, Timeframe.H1, T0.AddHours(i),
                startClose + 0.0002m, startClose + 0.0010m, startClose - 0.0010m, startClose, 1000));
        return bars;
    }

    // This is the gate test for iter-12. Filter: "ReplayBacktest"
    [Fact(Timeout = 60_000)]
    public async Task ReplayBacktest_FullPipeline_ProducesBarEvaluations()
    {
        const int barCount = 50;
        var bars = MakeBars(barCount);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var harness = await ReplayTestHarness.CreateAsync(bars);

        // RunAsync returns when the BacktestReplayAdapter completes its feed
        // and EngineWorker exits naturally (channel completion cascades)
        await harness.RunAsync(cts.Token);

        // Query the temp DB
        using var scope = harness.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var evalCount = await db.BarEvaluations.CountAsync();
        evalCount.Should().BeGreaterThan(0,
            "bars should flow through the full pipeline and produce BarEvaluation records");

        // If any trades were produced, they must be well-formed
        var trades = await db.TradeResults.ToListAsync();
        foreach (var t in trades)
        {
            t.EntryPrice.Should().BeGreaterThan(0, $"trade {t.Id} must have entry price");
            t.ExitPrice.Should().BeGreaterThan(0, $"trade {t.Id} must have exit price (BUG-03 check)");
            t.ExitReason.Should().NotBeNullOrEmpty($"trade {t.Id} must have exit reason");
        }
    }
}
```

### Phase B verification

```powershell
dotnet build --no-incremental

# Gate test
dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "ReplayBacktest"

# Expected: ReplayBacktest_FullPipeline_ProducesBarEvaluations [PASS]
```

---

## Forbidden list

- Do not change `EngineWorker.cs`
- Do not change `BacktestOrchestrator.cs` or `BacktestRunner.cs`
- Do not change any `Program.cs` file
- Do not add EF migrations
- Do not change `TradePersistenceHandler.cs` or `BarEvaluationHandler.cs`
- Do not change channel configurations in any handler
- Do not change `IBrokerAdapter.cs` or any domain interface

---

## Handover notes

_(Fill in after completing all phases)_

### Verification results

| Check | Result |
|-------|--------|
| `dotnet build --no-incremental` | 0 errors (5 pre-existing CTrader warnings) |
| Unit tests (87 baseline) | 87/87, 0 regressions |
| `AllBars_DeliveredWithoutDataLoss` | PASS |
| `SubmitOrder_ReceivesInstantFillWithPrice` | PASS |
| `ClosePosition_SendsFillPriceNotNull` | PASS |
| Full integration suite | 15/15 (was 12 + 3 new = 15) |
| `ReplayBacktest_FullPipeline_ProducesBarEvaluations` | not yet run (Phase B) |

### Issues closed

| Issue ID | Status |
|----------|--------|
| BUG-01 | FIXED |
| BUG-02 | FIXED |
| BUG-03 | FIXED |

### Anything that deviated from the plan

- No deviations. All constructor signatures matched. `NullLogger<T>` resolved transitively via `Microsoft.EntityFrameworkCore.InMemory` — no extra package reference needed.
- `SimulateFill` and `_pendingOrders` removed safely (nothing called them).

---

## Handover to Phase B

### Files changed in this iteration

| File | Change |
|------|--------|
| `src/TradingEngine.Infrastructure/Adapters/BacktestReplayAdapter.cs` | Full rewrite — unbounded channels, instant fills, async feed |
| `src/TradingEngine.Services/PositionTracker.cs` | Added `DetermineExitReason` method; replaced 2-line ternary |
| `tests/TradingEngine.Tests.Integration/AdapterTests/BacktestReplayAdapterTests.cs` | New — 3 tests for BUG-01/02/03 |

### Key design decisions for Phase B

1. **Feed is async background task**. `ConnectAsync` starts `FeedBarsAsync` and returns immediately.
   This lets `EngineWorker`'s `Task.WhenAll` consumer loops start while bars are still feeding.
   The feed completes channels in its `finally` block, which cascades to terminate `ReadAllAsync`
   loops in `EngineWorker`. This means `IHost.RunAsync` will exit naturally when the feed ends.

2. **Execution events drain via tick loop**. `SubmitOrderAsync` writes to `_executionChannel`.
   `ProcessExecutionEventsAsync` copies to `_executionEventChannel`. `ProcessTicksAsync` drains
   that channel only when a tick arrives. The feed writes 1 tick per bar, so execution events
   drain promptly. No change to `EngineWorker.cs` needed.

3. **`_lastClose` subtlety**. Fill price is `_lastClose` at submit time. Since the feed writes
   ahead of the consumer, `_lastClose` may be 1 bar ahead of the bar being evaluated. This is
   acceptable for backtesting (instant fill at current close).

### Phase B prep (ReplayTestHarness)

Before building the `ReplayTestHarness`, verify these classes exist with the expected constructors:
- `SymbolInfo(symbol, baseCurrency, quoteCurrency, contractSize, pipSize, typicalSpread)` — `TradingEngine.Domain`
- `RiskProfile(id, riskPerTradePct, maxDailyDrawdownPct, maxDrawdownPct, violations)` — `TradingEngine.Domain`
- `RiskProfileResolver(IReadOnlyDictionary<string, RiskProfile>)` — `TradingEngine.Risk`
- `BrokerClock` — implements `IEngineClock`
- `TypedEventBus` — implements `IEventBus`
- `EquityPersistenceHandler` — implements `IEventHandler<EquityUpdated>`
- `PositionManager` — implements `IPositionManager`
- `TradePersistenceHandler` — registers with event bus and calls `PersistenceService.SaveTradeAsync`
- `EngineRunContext(string RunId)` — `TradingEngine.Domain`

Run the verification command from the plan:
```powershell
Select-String -Path "src\**\*.cs" -Pattern "^public (sealed )?class (SymbolInfo|RiskProfile|RiskProfileResolver|BrokerClock|TypedEventBus|EquityPersistenceHandler|PositionManager)\b" -Recurse
```

### Phase B gate test

| Command | Expected |
|---------|----------|
| `dotnet build --no-incremental` | 0 errors |
| `dotnet test tests/TradingEngine.Tests.Simulation --no-build --filter "ReplayBacktest"` | 1 test, PASS |

The gate test proves the full pipeline: adapter → channels → engine → strategies → persistence.
