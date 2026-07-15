using TradingEngine.Domain.Experiments;

namespace TradingEngine.Tests.Unit.Experiments;

// P6.6 — the meta-allocator is a pure domain computation: per-cell metrics in → ranked allocation out.
// It replaces the win-rate-based rotation service with a contribution score that ranks cells by
// avg-R × √frequency, penalized for low sample confidence. All tests are fast, credential-free,
// and exercise the full parameter space including edge cases.
[Trait("Category", "Domain")]
public sealed class MetaAllocatorTests
{
    // ---- happy path ----

    [Fact]
    public void Allocate_RanksCells_ByContributionDescending()
    {
        var cells = new List<CellMetrics>
        {
            new("trend-breakout", "EURUSD", "H1", AvgR: 0.5, TotalTrades: 50, TradesPerWeek: 3.0, Enabled: true, Parked: false),
            new("ema-alignment", "EURUSD", "H1", AvgR: 0.2, TotalTrades: 30, TradesPerWeek: 2.0, Enabled: true, Parked: false),
            new("mean-reversion", "EURUSD", "H1", AvgR: 0.8, TotalTrades: 40, TradesPerWeek: 1.5, Enabled: true, Parked: false),
        };

        var result = MetaAllocator.Allocate(cells);

        result.Allocations.Should().BeInDescendingOrder(a => a.ContributionScore);
        result.Allocations.Should().HaveCount(3);
        result.CellsEvaluated.Should().Be(3);
    }

    [Fact]
    public void Allocate_NormalizesWeights_ToSumOne()
    {
        var cells = new List<CellMetrics>
        {
            new("a", "EURUSD", "H1", AvgR: 0.5, TotalTrades: 30, TradesPerWeek: 2.0, Enabled: true, Parked: false),
            new("b", "EURUSD", "H1", AvgR: 0.3, TotalTrades: 25, TradesPerWeek: 1.5, Enabled: true, Parked: false),
            new("c", "EURUSD", "H1", AvgR: 0.7, TotalTrades: 35, TradesPerWeek: 3.0, Enabled: true, Parked: false),
        };

        var result = MetaAllocator.Allocate(cells);

        var totalWeight = result.Allocations.Sum(a => a.Weight);
        totalWeight.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void Allocate_TopN_CellsAreKept()
    {
        var cells = new List<CellMetrics>
        {
            new("a", "EURUSD", "H1", AvgR: 0.9, TotalTrades: 50, TradesPerWeek: 3.0, Enabled: true, Parked: false),
            new("b", "EURUSD", "H1", AvgR: 0.7, TotalTrades: 40, TradesPerWeek: 2.5, Enabled: true, Parked: false),
            new("c", "EURUSD", "H1", AvgR: 0.5, TotalTrades: 30, TradesPerWeek: 2.0, Enabled: true, Parked: false),
            new("d", "EURUSD", "H1", AvgR: 0.2, TotalTrades: 60, TradesPerWeek: 1.0, Enabled: true, Parked: false),
            new("e", "EURUSD", "H1", AvgR: 0.1, TotalTrades: 70, TradesPerWeek: 0.5, Enabled: true, Parked: false),
        };

        var result = MetaAllocator.Allocate(cells, new MetaAllocatorOptions { TopN = 3 });

        result.Allocations.Take(3).Should().OnlyContain(a => a.Recommendation == "keep");
        result.Allocations.ElementAt(3).Recommendation.Should().NotBe("keep");
    }

    [Fact]
    public void Allocate_ParkThreshold_ParksLowScoreCells()
    {
        var cells = new List<CellMetrics>
        {
            new("a", "EURUSD", "H1", AvgR: 1.0, TotalTrades: 100, TradesPerWeek: 5.0, Enabled: true, Parked: false),
            new("b", "EURUSD", "H1", AvgR: 0.01, TotalTrades: 20, TradesPerWeek: 0.1, Enabled: true, Parked: false),
        };

        var result = MetaAllocator.Allocate(cells, new MetaAllocatorOptions { TopN = 1, ParkFraction = 0.5 });

        result.Allocations[0].Recommendation.Should().Be("keep");
        result.Allocations[1].Recommendation.Should().Be("park");
    }

    // ---- confidence penalty ----

    [Fact]
    public void Allocate_PenalizesLowSampleCells()
    {
        var cells = new List<CellMetrics>
        {
            new("a", "EURUSD", "H1", AvgR: 0.5, TotalTrades: 100, TradesPerWeek: 2.0, Enabled: true, Parked: false),
            new("b", "EURUSD", "H1", AvgR: 0.5, TotalTrades: 2, TradesPerWeek: 2.0, Enabled: true, Parked: false),
        };

        var result = MetaAllocator.Allocate(cells, new MetaAllocatorOptions { MinTrades = 10 });

        result.Allocations.Should().BeInDescendingOrder(a => a.ContributionScore);
        result.Allocations[0].StrategyId.Should().Be("a", "higher-confidence cell must rank higher at identical avgR and frequency");
    }

    [Fact]
    public void Allocate_ConfidenceFloor_PreventsZeroScore()
    {
        var cells = new List<CellMetrics>
        {
            new("a", "EURUSD", "H1", AvgR: 0.5, TotalTrades: 1, TradesPerWeek: 0.1, Enabled: true, Parked: false),
        };

        var result = MetaAllocator.Allocate(cells, new MetaAllocatorOptions { MinTrades = 50 });

        result.Allocations.Should().NotBeEmpty();
        result.Allocations[0].ContributionScore.Should().BeGreaterThan(0, "confidence floor of 0.5 prevents zero score");
    }

    // ---- negative edge ----

    [Fact]
    public void Allocate_NegativeAvgR_Flattened_NoAllocations()
    {
        // Negative avgR cells score 0 → filtered out entirely (no positive contribution).
        var cells = new List<CellMetrics>
        {
            new("a", "EURUSD", "H1", AvgR: -0.5, TotalTrades: 30, TradesPerWeek: 2.0, Enabled: true, Parked: false),
        };

        var result = MetaAllocator.Allocate(cells);

        result.Allocations.Should().BeEmpty();
        result.Summary.Should().Contain("No cells with positive contribution");
        result.CellsEvaluated.Should().Be(1);
    }

    // ---- edge cases ----

    [Fact]
    public void Allocate_EmptyInput_ReturnsEmpty()
    {
        var result = MetaAllocator.Allocate([]);

        result.Allocations.Should().BeEmpty();
        result.CellsEvaluated.Should().Be(0);
        result.Summary.Should().Contain("No cells");
    }

    [Fact]
    public void Allocate_AllDisabledOrParked_ReturnsEmpty()
    {
        var cells = new List<CellMetrics>
        {
            new("a", "EURUSD", "H1", AvgR: 0.5, TotalTrades: 30, TradesPerWeek: 2.0, Enabled: false, Parked: false),
            new("b", "EURUSD", "H1", AvgR: 0.3, TotalTrades: 20, TradesPerWeek: 1.0, Enabled: true, Parked: true),
        };

        var result = MetaAllocator.Allocate(cells);

        result.Allocations.Should().BeEmpty();
        result.Summary.Should().Contain("No enabled unparked");
    }

    [Fact]
    public void Allocate_AllNegativeEdge_ReturnsEmpty()
    {
        var cells = new List<CellMetrics>
        {
            new("a", "EURUSD", "H1", AvgR: -0.5, TotalTrades: 30, TradesPerWeek: 2.0, Enabled: true, Parked: false),
            new("b", "EURUSD", "H1", AvgR: -0.3, TotalTrades: 20, TradesPerWeek: 1.0, Enabled: true, Parked: false),
        };

        var result = MetaAllocator.Allocate(cells);

        result.Allocations.Should().BeEmpty();
        result.Summary.Should().Contain("No cells with positive contribution");
    }

    [Fact]
    public void Allocate_FewerCellsThanTopN_AllKeep()
    {
        var cells = new List<CellMetrics>
        {
            new("a", "EURUSD", "H1", AvgR: 0.5, TotalTrades: 30, TradesPerWeek: 2.0, Enabled: true, Parked: false),
        };

        var result = MetaAllocator.Allocate(cells, new MetaAllocatorOptions { TopN = 4 });

        result.Allocations.Should().HaveCount(1);
        result.Allocations[0].Recommendation.Should().Be("keep");
        result.Allocations[0].Weight.Should().Be(1.0);
    }

    [Fact]
    public void Allocate_UsesCustomOptions()
    {
        var cells = new List<CellMetrics>
        {
            new("a", "EURUSD", "H1", AvgR: 0.5, TotalTrades: 30, TradesPerWeek: 2.0, Enabled: true, Parked: false),
            new("b", "EURUSD", "H1", AvgR: 0.5, TotalTrades: 30, TradesPerWeek: 4.0, Enabled: true, Parked: false),
        };

        var highFreq = MetaAllocator.Allocate(cells, new MetaAllocatorOptions { FrequencyWeight = 1.0 });
        var lowFreq = MetaAllocator.Allocate(cells, new MetaAllocatorOptions { FrequencyWeight = 0.0 });

        highFreq.Allocations[0].StrategyId.Should().Be("b", "higher frequency weight = b (4/wk) > a (2/wk)");
        lowFreq.Allocations[0].ContributionScore.Should().Be(lowFreq.Allocations[1].ContributionScore,
            "zero frequency weight = pure avgR comparison -> equal scores for equal avgR");
    }
}
