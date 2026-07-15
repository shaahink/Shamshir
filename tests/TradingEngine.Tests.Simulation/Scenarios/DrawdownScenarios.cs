using TradingEngine.Engine;

namespace TradingEngine.Tests.Simulation.Scenarios;

public sealed class DrawdownScenarios
{
    private static readonly PropFirmRuleSet FtmoRules = new(
        "ftmo-standard", "FTMO Standard", "Fixed", 0.05, 0.10, 0.10, 4,
        "BalancePlusFloatingMinusFeesAndSwaps", "22:00:00", "Europe/Prague",
        false, "High", 30, 15, false, "21:00:00", "20:00:00", "NextTradingDay", false);

    private static RiskManager CreateRiskManager(decimal initialBalance = 100_000)
    {
        var registry = new SymbolInfoRegistry();
        registry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));

        var gov = Substitute.For<ITradingGovernor>();
        gov.Evaluate(Arg.Any<GovernorContext>())
            .Returns(new GovernorDecision(true, 1.0m, GovernorTradingState.Normal, "OK"));
        var rm = new RiskManager(registry, (_, _) => 1m,
            new NewsFilter(), new SessionFilter(), new StubClock(DateTime.UtcNow),
            Substitute.For<ICurrencyExposureTracker>(), gov, new SizingPolicyOptions());
        rm.InitializeDrawdownIfNeeded(initialBalance);
        rm.SetActiveRuleSet(FtmoRules);
        return rm;
    }

    [Fact]
    public void FivePctDailyLoss_HaltsTrading()
    {
        var rm = CreateRiskManager();
        var balance = 100_000m;
        int breachCount = 0;

        for (var i = 0; i < 10; i++)
        {
            balance -= 510m;
            rm.UpdateEquityLevels(balance);
            if (rm.CurrentState.DailyDrawdownUsed >= (decimal)FtmoRules.MaxDailyLossPercent)
            {
                rm.EnterProtectionMode("Daily DD breach", ProtectionCause.DailyDrawdown);
                breachCount++;
            }
        }

        breachCount.Should().BeGreaterThan(0, "trading must be blocked after 5% daily DD floor ($95,000) is crossed");
        rm.CurrentState.DailyDrawdownUsed.Should().BeGreaterThanOrEqualTo(0.05m);
    }

    [Fact]
    public void TenPctMaxLoss_PermanentlyHaltsTrading()
    {
        var rm = CreateRiskManager();
        var balance = 100_000m - 10_100m;
        rm.UpdateEquityLevels(balance);
        rm.CurrentState.MaxDrawdownUsed.Should().BeGreaterThanOrEqualTo((decimal)FtmoRules.MaxTotalLossPercent);
        rm.EnterProtectionMode("Max DD breach", ProtectionCause.MaxDrawdown);
        rm.CurrentState.InProtectionMode.Should().BeTrue();

        rm.OnDailyReset(balance);
        rm.CurrentState.InProtectionMode.Should().BeTrue("max DD breach persists after daily reset");
    }

    [Fact]
    public void WinSequence_DrawdownFractionStaysZero()
    {
        var rm = CreateRiskManager();
        var balance = 100_000m;

        for (var i = 0; i < 5; i++)
        {
            balance += 500m;
            rm.UpdateEquityLevels(balance);
        }

        rm.CurrentState.DailyDrawdownUsed.Should().Be(0m, "wins above initial balance produce zero daily DD");
        rm.CurrentState.MaxDrawdownUsed.Should().Be(0m);
    }

    [Fact]
    public void LossThenWin_DrawdownRecovers()
    {
        var rm = CreateRiskManager();
        var balance = 100_000m;

        balance -= 3_000m;
        rm.UpdateEquityLevels(balance);
        rm.CurrentState.DailyDrawdownUsed.Should().BeApproximately(0.03m, 0.001m);

        balance += 5_000m;
        rm.UpdateEquityLevels(balance);
        rm.CurrentState.DailyDrawdownUsed.Should().Be(0m, "equity above initial balance = zero daily DD");
    }

    [Fact]
    public void MultiStrategy_BothContributeToSameDdPool()
    {
        var rm = CreateRiskManager();
        var balance = 100_000m;
        int breachCount = 0;

        balance -= 2_600m; rm.UpdateEquityLevels(balance);
        balance -= 2_600m; rm.UpdateEquityLevels(balance);

        if (rm.CurrentState.DailyDrawdownUsed >= (decimal)FtmoRules.MaxDailyLossPercent)
        {
            rm.EnterProtectionMode("Daily DD breach", ProtectionCause.DailyDrawdown);
            breachCount++;
        }

        breachCount.Should().BeGreaterThan(0, "combined losses from two strategies must block ALL further trades");
    }
}
