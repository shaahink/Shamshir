namespace TradingEngine.Tests.Unit.Phase3ATests;

[Trait("Category", "Risk")]
public sealed class RiskManagerPhase3ATests
{
    private static readonly RiskProfile Profile = new(
        "standard", "Standard", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 5, false, "ftmo-standard");

    private static RiskManager Create()
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(100_000);
        var registry = new SymbolInfoRegistry();
        registry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return new RiskManager(tracker, registry, (_, _) => 1,
            new NewsFilter(), new SessionFilter(), new StubClock(DateTime.UtcNow),
            Substitute.For<ICurrencyExposureTracker>());
    }

    private static TradeIntent MakeIntent(string strategyId = "test") => new(
        Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
        new Price(1.08210m), new Price(1.08500m),
        strategyId, "standard", "Test", DateTime.UtcNow);

    private static EquitySnapshot Snap(decimal eq) =>
        new(DateTime.UtcNow, eq, 0, eq, eq, eq, 0, 0, EngineMode.Backtest);

    [Fact] // T-1
    public void ZeroEquity_ExposureCheck_BlocksTrade()
    {
        var violations = Create().Validate(MakeIntent(), Snap(0), Profile);
        violations.Should().Contain(v => v.Code == "MAX_EXPOSURE");
    }

    [Fact] // T-5
    public void PerStrategyPositionCap_BlocksAtLimit()
    {
        var rm = Create();
        for (int i = 0; i < 5; i++) rm.RegisterPosition(Guid.NewGuid(), "strat-a", 100);
        var violations = rm.Validate(MakeIntent("strat-a"), Snap(100_000), Profile with { MaxConcurrentPositions = 5 });
        violations.Should().Contain(v => v.Code == "STRATEGY_MAX_POSITIONS");
    }

    [Fact] // T-6
    public void MultiStrategyExposure_AggregatesCorrectly()
    {
        var rm = Create();
        rm.RegisterPosition(Guid.NewGuid(), "strat-a", 2000);
        rm.RegisterPosition(Guid.NewGuid(), "strat-b", 2000);
        var violations = rm.Validate(MakeIntent("strat-c"), Snap(100_000), Profile with { MaxExposurePercent = 0.03 });
        violations.Should().Contain(v => v.Code == "MAX_EXPOSURE");
    }

    [Fact] // T-15
    public void DailyDrawdown_UsesInitialAccountBalance()
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(100_000);
        tracker.GetDailyLossLimit(0.05m).Should().Be(95_000m);
    }
}
