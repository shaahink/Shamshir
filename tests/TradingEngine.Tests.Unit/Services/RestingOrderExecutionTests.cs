using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Services;

namespace TradingEngine.Tests.Unit.Services;

/// <summary>
/// F40 (iter-alpha-loop). cTrader answers a limit/stop entry it has RESTED with state "Pending".
/// <see cref="OrderState"/> had no such member, so <c>CTraderBrokerAdapter.ParseExecution</c> threw and the
/// adapter abandoned the entire <c>bar_result</c> it appeared in — taking any sibling fills and the bar's
/// account update with it. Meanwhile the tracker's fallback arm mapped any unrecognised state to
/// <c>OrderFilled</c> with <c>FillPrice ?? 0</c>, so had the parse ever succeeded a rested order would have
/// been booked as a fill at price ZERO.
///
/// The contract these pin: a resting acknowledgement is NOT an execution. The tape says nothing until such
/// an order fills or expires, and the venue leg must behave identically or the two execution streams cannot
/// be compared at all.
/// </summary>
public sealed class RestingOrderExecutionTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static PositionTracker CreateTracker()
    {
        var symbolRegistry = Substitute.For<ISymbolInfoRegistry>();
        symbolRegistry.Get(Eurusd).Returns(EurusdInfo);

        var clock = Substitute.For<IEngineClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);

        return new PositionTracker(
            symbolRegistry, (_, _) => 1.0m,
            Substitute.For<IRiskManager>(), Substitute.For<IPositionManager>(),
            Substitute.For<IEventBus>(), clock, NullLogger<PositionTracker>.Instance);
    }

    private static Guid TrackAnOrder(PositionTracker tracker)
    {
        var orderId = Guid.NewGuid();
        var intent = new TradeIntent(
            Eurusd, TradeDirection.Long, OrderType.Limit,
            new Price(1.1000m), new Price(1.0900m), new Price(1.1100m),
            "test", "standard", "test", DateTime.UtcNow);

        tracker.TrackOrder(orderId, new OrderRequest(
            intent, 1.0m, Eurusd, TradeDirection.Long, OrderType.Limit, new Price(1.1000m)), 10m);

        return orderId;
    }

    // The venue's word for "I am holding your limit order" must parse. Without this member the adapter
    // threw on every resting entry, discarding the whole batch of executions it arrived in.
    [Fact]
    public void Venue_pending_state_parses()
    {
        Assert.Equal(OrderState.Pending, Enum.Parse<OrderState>("Pending", ignoreCase: true));
    }

    [Fact]
    public async Task Resting_acknowledgement_is_not_an_execution()
    {
        var tracker = CreateTracker();
        var orderId = TrackAnOrder(tracker);

        var pending = new ExecutionEvent(orderId, OrderState.Pending, null, 0m, null, DateTime.UtcNow);
        var effects = await tracker.OnExecutionAsync(pending, []);

        Assert.Null(effects);   // never a fill — and never a fill at price zero
    }

    /// <summary>
    /// The ordering trap: the tracker's duplicate guard marks an order as "processed" on first sight. Had
    /// the Pending acknowledgement been allowed to fall through into that bookkeeping, the order's REAL
    /// fill would have arrived looking like a duplicate and been dropped — silently losing the trade.
    /// </summary>
    [Fact]
    public async Task Pending_then_fill_still_fills()
    {
        var tracker = CreateTracker();
        var orderId = TrackAnOrder(tracker);

        await tracker.OnExecutionAsync(
            new ExecutionEvent(orderId, OrderState.Pending, null, 0m, null, DateTime.UtcNow), []);

        var fill = new ExecutionEvent(
            orderId, OrderState.Filled, new Price(1.1000m), 1.0m, null, DateTime.UtcNow);
        await tracker.OnExecutionAsync(fill, []);

        var open = tracker.OpenPositions.Values.SingleOrDefault(p => p.OrderId == orderId);
        Assert.NotNull(open);
        Assert.Equal(1.1000m, open!.EntryPrice.Value);
    }
}
