namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Services")]
public sealed class OrderDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_WithValidIntent_ReturnsOrderContext()
    {
        var rm = Substitute.For<IRiskManager>();
        rm.Validate(Arg.Any<TradeIntent>(), Arg.Any<EquitySnapshot>(), Arg.Any<RiskProfile>(), Arg.Any<decimal>())
            .Returns([]);
        rm.CalculateLotSize(Arg.Any<TradeIntent>(), Arg.Any<EquitySnapshot>(), Arg.Any<RiskProfile>(), Arg.Any<decimal>())
            .Returns(0.1m);
        rm.ValidateBudgetEntry(Arg.Any<decimal>(), Arg.Any<EquitySnapshot>(), Arg.Any<decimal>())
            .Returns(true);

        var resolver = Substitute.For<IRiskProfileResolver>();
        resolver.Resolve(Arg.Any<string>()).Returns(new RiskProfile("test", "Test", 0.01, 0.05, 0.10, 100, 0.05, 0.5, 0.5, 3, false, "ftmo-standard"));

        var registry = new SymbolInfoRegistry();
        registry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));

        var logger = Substitute.For<ILogger<OrderDispatcher>>();
        var journal = Substitute.For<IDecisionJournal>();
        var runCtx = new EngineRunContext("test-run");
        var dispatcher = new OrderDispatcher(rm, resolver, registry, (_, _) => 1, journal, runCtx, logger);
        var broker = Substitute.For<IBrokerAdapter>();
        broker.SubmitOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());

        var intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
            new Price(1.08210m), new Price(1.08500m), "test", "standard", "", DateTime.UtcNow);
        var equity = new EquitySnapshot(DateTime.UtcNow, 100_000, 0, 100_000, 100_000, 100_000, 0, 0, EngineMode.Backtest);

        var result = await dispatcher.DispatchAsync(intent, equity, 1.0850m, broker, [], CancellationToken.None);

        result.Should().NotBeNull();
        result!.Lots.Should().Be(0.1m);
        await broker.Received(1).SubmitOrderAsync(Arg.Any<OrderRequest>(), Arg.Any<CancellationToken>());
    }
}
