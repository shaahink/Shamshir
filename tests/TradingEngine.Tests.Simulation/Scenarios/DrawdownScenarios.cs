namespace TradingEngine.Tests.Simulation.Scenarios;

public sealed class DrawdownScenarios
{
    [Fact]
    public void FivePctDailyLoss_HaltsTrading()
    {
        var harness = new DrawdownTestHarness(initialBalance: 100_000);

        for (var i = 0; i < 10; i++)
            harness.InjectLoss(amountUsd: 510m);

        harness.TradesAfterDailyLimitBreached.Should().BeGreaterThan(0,
            "trading must be blocked after 5% daily DD floor ($95,000) is crossed");
        harness.DailyDdFraction.Should().BeGreaterThanOrEqualTo(0.05m);
    }

    [Fact]
    public void TenPctMaxLoss_PermanentlyHaltsTrading()
    {
        var harness = new DrawdownTestHarness(initialBalance: 100_000);

        harness.InjectLoss(amountUsd: 10_100m);
        harness.IsInProtectionMode.Should().BeTrue("equity below 10% max DD floor must enter protection");

        harness.SimulateDailyReset();

        harness.IsInProtectionMode.Should().BeTrue(
            "max DD breach does not lift on daily reset — protection should persist");
    }

    [Fact]
    public void WinSequence_DrawdownFractionStaysZero()
    {
        var harness = new DrawdownTestHarness(initialBalance: 100_000);

        for (var i = 0; i < 5; i++)
            harness.InjectWin(amountUsd: 500m);

        harness.DailyDdFraction.Should().Be(0m, "wins above initial balance produce zero daily DD");
        harness.MaxDdFraction.Should().Be(0m);
    }

    [Fact]
    public void LossThenWin_DrawdownRecovers()
    {
        var harness = new DrawdownTestHarness(initialBalance: 100_000);

        harness.InjectLoss(amountUsd: 3_000m);
        harness.DailyDdFraction.Should().BeApproximately(0.03m, 0.001m);

        harness.InjectWin(amountUsd: 5_000m);
        harness.DailyDdFraction.Should().Be(0m, "equity above initial balance = zero daily DD");
    }

    [Fact]
    public void MultiStrategy_BothContributeToSameDdPool()
    {
        var harness = new DrawdownTestHarness(initialBalance: 100_000);

        harness.InjectLoss(amountUsd: 2_600m, strategyId: "strat-1");
        harness.InjectLoss(amountUsd: 2_600m, strategyId: "strat-2");

        harness.TradesAfterDailyLimitBreached.Should().BeGreaterThan(0,
            "combined losses from two strategies must block ALL further trades");
    }
}
