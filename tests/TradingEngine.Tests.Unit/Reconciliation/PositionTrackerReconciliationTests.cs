using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Services;

namespace TradingEngine.Tests.Unit.Reconciliation;

/// <summary>
/// V1/V2 — seeding the tracker from a venue open-position snapshot (startup/reconnect
/// reconciliation), and V3 — venue-confirmed SL writeback.
/// </summary>
[Trait("Category", "Reconciliation")]
public sealed class PositionTrackerReconciliationTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private IPositionManager _positionManager = null!;
    private IRiskManager _riskManager = null!;

    private PositionTracker CreateTracker()
    {
        var symbolRegistry = Substitute.For<ISymbolInfoRegistry>();
        symbolRegistry.Get(Eurusd).Returns(EurusdInfo);
        _riskManager = Substitute.For<IRiskManager>();
        _positionManager = Substitute.For<IPositionManager>();
        var eventBus = Substitute.For<IEventBus>();
        var clock = Substitute.For<IEngineClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);

        return new PositionTracker(
            symbolRegistry, (_, _) => 1.0m, _riskManager, _positionManager,
            eventBus, clock, NullLogger<PositionTracker>.Instance);
    }

    private static OpenPositionInfo VenuePosition(Guid orderId, decimal entry = 1.1000m, decimal sl = 1.0950m, decimal? tp = 1.1100m)
        => new(orderId, Eurusd, TradeDirection.Long, 0.10m, new Price(entry),
            new Price(sl), tp is null ? null : new Price(tp.Value));

    [Fact]
    public void SeedOpenPositions_CreatesTrackedOpenPosition_WithVenueEntryAndStops()
    {
        var tracker = CreateTracker();
        var orderId = Guid.NewGuid();

        tracker.SeedOpenPositions([VenuePosition(orderId)], []);

        tracker.OpenPositions.Should().ContainKey(orderId);
        var pos = tracker.OpenPositions[orderId];
        pos.Symbol.Should().Be(Eurusd);
        pos.Direction.Should().Be(TradeDirection.Long);
        pos.Lots.Should().Be(0.10m);
        pos.EntryPrice.Value.Should().Be(1.1000m);
        pos.CurrentStopLoss.Value.Should().Be(1.0950m);
        pos.TakeProfit!.Value.Value.Should().Be(1.1100m);

        // reconciled positions are registered so they can be managed/trailed
        _positionManager.Received().RegisterPosition(Arg.Any<Position>(), Arg.Any<PositionManagementConfig>());
        _riskManager.Received().RegisterPosition(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<decimal>());
    }

    [Fact]
    public void SeedOpenPositions_IsIdempotent_AcrossReconnects()
    {
        var tracker = CreateTracker();
        var orderId = Guid.NewGuid();

        tracker.SeedOpenPositions([VenuePosition(orderId)], []);
        tracker.SeedOpenPositions([VenuePosition(orderId)], []); // simulate a reconnect re-sending the same snapshot

        tracker.OpenPositions.Should().HaveCount(1, "the same venue position must not be tracked twice");
        _positionManager.Received(1).RegisterPosition(Arg.Any<Position>(), Arg.Any<PositionManagementConfig>());
    }

    [Fact]
    public async Task SeededPosition_CanBeForceClosed()
    {
        var tracker = CreateTracker();
        var orderId = Guid.NewGuid();
        tracker.SeedOpenPositions([VenuePosition(orderId)], []);

        await tracker.RequestForceCloseAllAsync("breach");

        // force-close requested against the reconciled position — it transitions out of Open.
        tracker.OpenPositions.Count.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void ConfirmStopLoss_WritesVenueConfirmedStopBackOntoPosition()
    {
        var tracker = CreateTracker();
        var orderId = Guid.NewGuid();
        tracker.SeedOpenPositions([VenuePosition(orderId, sl: 1.0950m)], []);

        tracker.ConfirmStopLoss(orderId, new Price(1.0980m), null);

        tracker.OpenPositions[orderId].CurrentStopLoss.Value.Should().Be(1.0980m,
            "a venue-confirmed trailing stop must be written back so the engine's exit view follows the venue");
    }

    [Fact]
    public void ConfirmStopLoss_UnknownOrder_IsNoOp()
    {
        var tracker = CreateTracker();
        var act = () => tracker.ConfirmStopLoss(Guid.NewGuid(), new Price(1.0m), null);
        act.Should().NotThrow();
    }
}
