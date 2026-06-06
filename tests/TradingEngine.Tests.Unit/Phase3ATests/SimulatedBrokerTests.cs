namespace TradingEngine.Tests.Unit.Phase3ATests;

[Trait("Category", "Infrastructure")]
public sealed class SimulatedBrokerPhase3ATests
{
    [Fact]
    public void OnTickReceived_FillsPendingOrders()
    {
        var broker = new SimulatedBrokerAdapter();
        var tick = new Tick(Symbol.Parse("EURUSD"), 1.08500m, 1.08510m, DateTime.UtcNow);

        _ = broker.SubmitOrderAsync(new OrderRequest(
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

    [Fact]
    public void OnTickReceived_Fill_EmitsAccountUpdate()
    {
        var broker = new SimulatedBrokerAdapter();
        var tick = new Tick(Symbol.Parse("EURUSD"), 1.08500m, 1.08510m, DateTime.UtcNow);

        _ = broker.SubmitOrderAsync(new OrderRequest(
            new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
                new Price(1.08300m), new Price(1.08700m), "test", "standard", "", DateTime.UtcNow),
            0.1m, Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null),
            CancellationToken.None);

        broker.OnTickReceived(tick);

        var hasAccountUpdate = broker.AccountStream.TryRead(out var acct);
        hasAccountUpdate.Should().BeTrue("fill must emit AccountUpdate");
        acct!.Balance.Should().Be(100_000m, "fill does not change balance");
    }

    [Fact]
    public void OnTickReceived_SlHit_EmitsAccountUpdateWithLoss()
    {
        var broker = new SimulatedBrokerAdapter();
        var entryTick = new Tick(Symbol.Parse("EURUSD"), 1.08000m, 1.08010m, DateTime.UtcNow);

        _ = broker.SubmitOrderAsync(new OrderRequest(
            new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
                new Price(1.07500m), new Price(1.09000m), "test", "standard", "", DateTime.UtcNow),
            0.1m, Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null),
            CancellationToken.None);

        broker.OnTickReceived(entryTick);
        broker.AccountStream.TryRead(out _);

        var slTick = new Tick(Symbol.Parse("EURUSD"), 1.07500m, 1.07510m, DateTime.UtcNow);
        broker.OnTickReceived(slTick);

        var closeAcct = broker.AccountStream.TryRead(out var acct);
        closeAcct.Should().BeTrue("SL hit must emit AccountUpdate");
        acct!.Balance.Should().BeLessThan(100_000m, "loss must reduce balance");
    }

    [Fact]
    public void OnTickReceived_TpHit_EmitsAccountUpdateWithProfit()
    {
        var broker = new SimulatedBrokerAdapter();
        var entryTick = new Tick(Symbol.Parse("EURUSD"), 1.08000m, 1.08010m, DateTime.UtcNow);

        _ = broker.SubmitOrderAsync(new OrderRequest(
            new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
                new Price(1.07500m), new Price(1.09000m), "test", "standard", "", DateTime.UtcNow),
            0.1m, Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null),
            CancellationToken.None);

        broker.OnTickReceived(entryTick);
        broker.AccountStream.TryRead(out _);

        var tpTick = new Tick(Symbol.Parse("EURUSD"), 1.09000m, 1.09010m, DateTime.UtcNow);
        broker.OnTickReceived(tpTick);

        var closeAcct = broker.AccountStream.TryRead(out var acct);
        closeAcct.Should().BeTrue("TP hit must emit AccountUpdate");
        acct!.Balance.Should().BeGreaterThan(100_000m, "win must increase balance");
    }

    [Fact]
    public void OnTickReceived_MultiplePositions_BalanceAccumulates()
    {
        var broker = new SimulatedBrokerAdapter();
        var entryTick = new Tick(Symbol.Parse("EURUSD"), 1.08000m, 1.08010m, DateTime.UtcNow);

        _ = broker.SubmitOrderAsync(new OrderRequest(
            new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
                new Price(1.07500m), new Price(1.09000m), "test", "standard", "", DateTime.UtcNow),
            0.1m, Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null),
            CancellationToken.None);

        _ = broker.SubmitOrderAsync(new OrderRequest(
            new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
                new Price(1.07500m), new Price(1.09000m), "test", "standard", "", DateTime.UtcNow),
            0.1m, Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null),
            CancellationToken.None);

        broker.OnTickReceived(entryTick);
        broker.AccountStream.TryRead(out _);
        broker.AccountStream.TryRead(out _);

        var tpTick = new Tick(Symbol.Parse("EURUSD"), 1.09000m, 1.09010m, DateTime.UtcNow);
        broker.OnTickReceived(tpTick);

        var updates = new List<AccountUpdate>();
        while (broker.AccountStream.TryRead(out var u))
            updates.Add(u);

        updates.Should().HaveCount(c => c >= 2);
        updates.Last().Balance.Should().BeGreaterThan(100_000m + 50m);
    }
}
