namespace TradingEngine.Tests.Unit.RiskTests;

[Trait("Category", "Risk")]
public sealed class RiskManagerTests
{
    private static readonly RiskProfile Profile = new(
        "standard", "Standard", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 5, false, "ftmo-standard");

    private static RiskManager CreateRiskManager()
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(100_000);
        var registry = new SymbolInfoRegistry();
        registry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return new RiskManager(tracker, registry, (_, _) => 1,
            new NewsFilter(), new SessionFilter(), new StubClock(DateTime.UtcNow));
    }

    private static EquitySnapshot CreateSnapshot(decimal equity, decimal dailyDd = 0, decimal maxDd = 0) => new(
        DateTime.UtcNow, equity, 0, equity, equity, equity, dailyDd, maxDd, EngineMode.Backtest);

    private static TradeIntent CreateIntent() => new(
        Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
        new Price(1.08210m), new Price(1.08500m),
        "test-strategy", "standard", "Test", DateTime.UtcNow);

    [Fact]
    public void Validate_ProtectionModeActive_BlocksAllTrades()
    {
        var rm = CreateRiskManager();
        rm.EnterProtectionMode("Test breach", ProtectionCause.MaxDrawdown);
        var violations = rm.Validate(CreateIntent(), CreateSnapshot(100_000), Profile);
        violations.Should().Contain(v => v.Code == "PROTECTION_MODE_ACTIVE");
    }

    [Fact]
    public void Validate_DailyDDReached_BlocksTrades()
    {
        var rm = CreateRiskManager();
        rm.SetActiveRuleSet(new PropFirmRuleSet(
            "ftmo-standard", "FTMO Standard", "Fixed", 0.05, 0.10, 0.10, 4,
            "BalancePlusFloatingMinusFeesAndSwaps", "22:00:00", "Europe/Prague",
            false, "High", 30, 15, false, "21:00:00", "20:00:00", "NextTradingDay", false));
        var violations = rm.Validate(CreateIntent(), CreateSnapshot(100_000, dailyDd: 0.06m), Profile);
        violations.Should().Contain(v => v.Code == "DAILY_DD_LIMIT");
    }

    [Fact]
    public void Validate_MaxDDReached_BlocksTrades()
    {
        var rm = CreateRiskManager();
        rm.SetActiveRuleSet(new PropFirmRuleSet(
            "ftmo-standard", "FTMO Standard", "Fixed", 0.05, 0.10, 0.10, 4,
            "BalancePlusFloatingMinusFeesAndSwaps", "22:00:00", "Europe/Prague",
            false, "High", 30, 15, false, "21:00:00", "20:00:00", "NextTradingDay", false));
        var violations = rm.Validate(CreateIntent(), CreateSnapshot(100_000, maxDd: 0.12m), Profile);
        violations.Should().Contain(v => v.Code == "MAX_DD_LIMIT");
    }

    [Fact]
    public void Validate_MultipleViolations_ReturnsAll()
    {
        var rm = CreateRiskManager();
        rm.SetActiveRuleSet(new PropFirmRuleSet(
            "ftmo-standard", "FTMO Standard", "Fixed", 0.05, 0.10, 0.10, 4,
            "BalancePlusFloatingMinusFeesAndSwaps", "22:00:00", "Europe/Prague",
            false, "High", 30, 15, false, "21:00:00", "20:00:00", "NextTradingDay", false));
        var violations = rm.Validate(CreateIntent(), CreateSnapshot(100_000, dailyDd: 0.06m, maxDd: 0.12m), Profile);
        violations.Should().Contain(v => v.Code == "DAILY_DD_LIMIT");
        violations.Should().Contain(v => v.Code == "MAX_DD_LIMIT");
    }
}
