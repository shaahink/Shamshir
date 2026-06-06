namespace TradingEngine.Tests.Unit.Phase3ATests;

[Trait("Category", "Infrastructure")]
public sealed class SimulatedBrokerPhase3ATests
{
    [Fact] // T-7
    public void OnTickReceived_FillsPendingOrders()
    {
        var broker = new SimulatedBrokerAdapter();
        var tick = new Tick(Symbol.Parse("EURUSD"), 1.08500m, 1.08510m, DateTime.UtcNow);

        broker.SubmitOrderAsync(new OrderRequest(
            new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
                new Price(1.08300m), new Price(1.08700m), "test", "standard", "", DateTime.UtcNow),
            0.1m, Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null),
            CancellationToken.None);

        broker.OnTickReceived(tick);

        var found = broker.ExecutionStream.TryRead(out var exec);
        found.Should().BeTrue();
        exec.Should().NotBeNull();
        exec!.NewState.Should().Be(OrderState.Filled);
    }
}
