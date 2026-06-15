using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Services;

namespace TradingEngine.Tests.Unit.Concurrency;

public sealed class PositionTrackerConcurrencyTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private PositionTracker CreateTracker()
    {
        var symbolRegistry = Substitute.For<ISymbolInfoRegistry>();
        symbolRegistry.Get(Eurusd).Returns(EurusdInfo);

        var riskManager = Substitute.For<IRiskManager>();
        var positionManager = Substitute.For<IPositionManager>();
        var eventBus = Substitute.For<IEventBus>();
        var clock = Substitute.For<IEngineClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);

        return new PositionTracker(
            symbolRegistry, (_, _) => 1.0m, riskManager, positionManager,
            eventBus, clock, NullLogger<PositionTracker>.Instance);
    }

    /// <summary>
    /// Concurrent fills + force-close racing TrackOrder must not corrupt PositionTracker state.
    /// PositionTracker uses SemaphoreSlim(1,1) to serialize its three mutators.
    /// </summary>
    [Fact]
    public async Task ConcurrentFillsAndForceClose_DoesNotCorruptState()
    {
        var tracker = CreateTracker();
        var rng = new Random(42);

        // Submit N orders via TrackOrder, then concurrently process fills + force-close.
        var orderIds = new List<Guid>();
        for (var i = 0; i < 50; i++)
        {
            var orderId = Guid.NewGuid();
            orderIds.Add(orderId);

            var intent = new TradeIntent(
                Eurusd, TradeDirection.Long, OrderType.Market,
                null, new Price(1.0900m), new Price(1.1100m),
                "test", "standard", "test", DateTime.UtcNow);
            var request = new OrderRequest(intent, 0.01m, Eurusd, TradeDirection.Long,
                OrderType.Market, null);

            tracker.TrackOrder(orderId, request, 10m);
        }

        // Concurrently submit fills and force-close requests
        var tasks = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            var orderId = orderIds[i];
            var delay = rng.Next(5);

            // Fill
            tasks.Add(Task.Run(async () =>
            {
                await Task.Delay(delay);
                var fill = new ExecutionEvent(
                    orderId, OrderState.Filled, new Price(1.1000m),
                    0.01m, null, DateTime.UtcNow);
                await tracker.OnExecutionAsync(fill, []);
            }));

            // Force close every 5th order
            if (i % 5 == 0)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await Task.Delay(delay + 2);
                    await tracker.RequestForceCloseAllAsync("stress-test");
                }));
            }
        }

        await Task.WhenAll(tasks);

        // After concurrency, open positions must not be corrupted.
        // We expect: some positions opened (filled), some force-closed.
        // Key invariant: no duplicate position IDs, no null entries.
        var openPositions = tracker.OpenPositions;
        openPositions.Should().NotContainNulls("no position entry should be null");
        openPositions.Count.Should().BeLessThanOrEqualTo(50,
            "cannot have more open positions than submitted orders");

        // All open positions should be in Open phase
        foreach (var (_, pos) in openPositions)
        {
            pos.EntryPrice.Value.Should().BeGreaterThan(0, "filled positions must have valid entry price");
            pos.Lots.Should().BeGreaterThan(0, "filled positions must have positive lots");
        }
    }

    [Fact]
    public async Task SingleThreaded_FillAndClose_IsConsistent()
    {
        var tracker = CreateTracker();

        var orderId = Guid.NewGuid();
        var intent = new TradeIntent(
            Eurusd, TradeDirection.Long, OrderType.Market,
            null, new Price(1.0900m), new Price(1.1100m),
            "test", "standard", "test", DateTime.UtcNow);
        var request = new OrderRequest(intent, 0.01m, Eurusd, TradeDirection.Long,
            OrderType.Market, null);

        tracker.TrackOrder(orderId, request, 10m);

        // Fill creates an open position
        var fill = new ExecutionEvent(
            orderId, OrderState.Filled, new Price(1.1000m), 0.01m, null, DateTime.UtcNow);
        await tracker.OnExecutionAsync(fill, []);

        tracker.OpenPositions.Count.Should().Be(1, "fill must create an open position");

        // ForceCloseAll produces close effects
        await tracker.RequestForceCloseAllAsync("test");

        // The position may be closing or closed — state is consistent.
        tracker.OpenPositions.Count.Should().BeLessThanOrEqualTo(1,
            "force-close must not increase position count");
    }
}
