using TradingEngine.Engine;

namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Engine")]
[Trait("Speed", "Fast")]
public sealed class RiskGateTests
{
    [Fact]
    public void ProjectWorstCase_CandidateAlone_WouldNotBreach_ReturnsPassed()
    {
        var result = RiskGate.ProjectWorstCase(
            currentEquity: 100_000,
            dailyStartEquity: 100_000,
            initialBalance: 100_000,
            maxDailyLossPercent: 0.05m,
            maxTotalLossPercent: 0.10m,
            drawdownType: "Fixed",
            slPips: 20,
            lots: 0.1m,
            pipValuePerLot: 10m,
            openPositions: []);

        result.Should().Be(RiskGate.Passed);
    }

    [Fact]
    public void ProjectWorstCase_CandidateAlone_BreachesDailyDD_ReturnsWorstCaseDDWouldBreachDaily()
    {
        var result = RiskGate.ProjectWorstCase(
            currentEquity: 100_000,
            dailyStartEquity: 100_000,
            initialBalance: 100_000,
            maxDailyLossPercent: 0.01m,
            maxTotalLossPercent: 0.10m,
            drawdownType: "Fixed",
            slPips: 150,
            lots: 10m,
            pipValuePerLot: 10m,
            openPositions: []);

        result.Should().Be(RiskGate.WorstCaseDDWouldBreachDaily);
    }

    [Fact]
    public void ProjectWorstCase_WithOpenPositions_CombinedWouldBreach_ReturnsBlocked()
    {
        var openPositions = new List<ProjectedPosition>
        {
            new(50, 1m, 10m),
            new(40, 1m, 10m),
        };

        var result = RiskGate.ProjectWorstCase(
            currentEquity: 100_000,
            dailyStartEquity: 100_000,
            initialBalance: 100_000,
            maxDailyLossPercent: 0.01m,
            maxTotalLossPercent: 0.10m,
            drawdownType: "Fixed",
            slPips: 30,
            lots: 1m,
            pipValuePerLot: 10m,
            openPositions);

        result.Should().Be(RiskGate.WorstCaseDDWouldBreachDaily);
    }

    [Fact]
    public void ProjectWorstCase_BreachesOverallDD_ReturnsWorstCaseDDWouldBreachOverall()
    {
        var result = RiskGate.ProjectWorstCase(
            currentEquity: 100_000,
            dailyStartEquity: 100_000,
            initialBalance: 100_000,
            maxDailyLossPercent: 0.20m,
            maxTotalLossPercent: 0.05m,
            drawdownType: "Fixed",
            slPips: 200,
            lots: 5m,
            pipValuePerLot: 10m,
            openPositions: []);

        result.Should().Be(RiskGate.WorstCaseDDWouldBreachOverall);
    }
}
