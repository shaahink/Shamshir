namespace TradingEngine.Tests.Unit.Risk;

[Trait("Category", "Risk")]
public sealed class ComplianceServiceTests
{
    private static readonly PropFirmRuleSet Rules = new(
        "ftmo-standard", "FTMO Standard", "Fixed",
        0.05, 0.10, 0.10, 4,
        "BalancePlusFloatingMinusFeesAndSwaps", "22:00:00", "Europe/Prague",
        false, "High", 30, 15, false, "21:00:00", "20:00:00", "NextTradingDay", false)
    {
        MaxWeeklyLossPercent = 0.04,
        MaxMonthlyLossPercent = 0.08,
        RequireProfitTarget = true,
    };

    private static (PropFirmComplianceService, DrawdownTracker) Make(decimal initial = 100_000)
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(initial);
        var estimator = Substitute.For<IPassProbabilityEstimator>();
        var clock = new StubClock(DateTime.UtcNow);
        var svc = new PropFirmComplianceService(Rules, tracker, clock, estimator);
        return (svc, tracker);
    }

    [Fact]
    public void WeeklyDDLimit_Blocks_WhenWeeklyLossExceeds4Pct()
    {
        var (svc, tracker) = Make();
        tracker.OnEquityUpdate(95_000);
        var state = new ExtendedRiskState { WeeklyDrawdownUsed = 0.05m };

        var result = svc.ValidateSignal(null!, state, null!);
        result.Severity.Should().Be(ComplianceSeverity.Block);
    }

    [Fact]
    public void MonthlyDDLimit_Blocks_WhenMonthlyLossExceeds8Pct()
    {
        var (svc, _) = Make();
        var state = new ExtendedRiskState { MonthlyDrawdownUsed = 0.09m };

        var result = svc.ValidateSignal(null!, state, null!);
        result.Severity.Should().Be(ComplianceSeverity.Block);
    }

    [Fact]
    public void PassProbability_ReturnsHighProbability_WhenEquityOnTrack()
    {
        var estimator = Substitute.For<IPassProbabilityEstimator>();
        var expected = new PassProbabilityEstimate { ProbabilityOfPass = 0.85 };
        estimator.Estimate(Arg.Any<PassProbabilityInput>()).Returns(expected);

        var clock = new StubClock(DateTime.UtcNow);
        var tracker = new DrawdownTracker(); tracker.Initialize(100_000);
        var svc = new PropFirmComplianceService(Rules, tracker, clock, estimator);

        var result = svc.EstimatePassProbability(new PassProbabilityInput());
        result.ProbabilityOfPass.Should().Be(0.85);
    }

    [Fact]
    public void PassProbability_ReturnsZero_WhenHistoryEmpty()
    {
        var tracker = new DrawdownTracker(); tracker.Initialize(100_000);
        var estimator = new PassProbabilityEstimator();
        var clock = new StubClock(DateTime.UtcNow);
        var svc = new PropFirmComplianceService(Rules, tracker, clock, estimator);

        var result = svc.EstimatePassProbability(new PassProbabilityInput { HistoricalDailyPnL = [] });
        result.ProbabilityOfPass.Should().Be(0);
    }

    [Fact]
    public void ValidSignal_Passes_WhenNoLimitsExceeded()
    {
        var (svc, _) = Make();
        var state = new ExtendedRiskState();

        var result = svc.ValidateSignal(null!, state, null!);
        result.Passed.Should().BeTrue();
    }
}
