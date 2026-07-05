namespace TradingEngine.Tests.Unit.Phase33Tests;

using TradingEngine.Services.ExitLab;

/// <summary>
/// P3.3 exit grid evaluator tests — parallelism, cell coverage, aggregate correctness.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ExitGridEvaluatorTests
{
    private static List<TradeExcursionInput> TwoLongTrades()
    {
        var tpHit = new TradeExcursionInput
        {
            Direction = TradeDirection.Long,
            EntryPrice = 1.1000m,
            InitialStopLoss = new Price(1.0970m),
            PipSize = 0.0001m,
            SpreadPips = 2.0,
            Path = new List<ExcursionPoint>
            {
                new(0, 5.0, -10.0),
                new(60, 35.0, -5.0),
                new(120, 65.0, -25.0), // TP: 65 >= 60, SL not hit (lo=-25 > -30)
            },
        };

        var slHit = new TradeExcursionInput
        {
            Direction = TradeDirection.Long,
            EntryPrice = 1.1000m,
            InitialStopLoss = new Price(1.0970m),
            PipSize = 0.0001m,
            SpreadPips = 2.0,
            Path = new List<ExcursionPoint>
            {
                new(0, 2.0, -15.0),
                new(60, 3.0, -32.0), // SL hit: lo=-32 <= -30
            },
        };

        return [tpHit, slHit];
    }

    [Fact]
    public void Evaluate_SingleCell_ReturnsCorrectAggregate()
    {
        var rules = new List<ExitRule>
        {
            new() { SlAtrMultiple = 1.5, TpRrMultiple = 2.0, ReferenceAtrPips = 20.0 },
        };

        var cells = ExitGridEvaluator.Evaluate(TwoLongTrades(), rules);

        cells.Should().HaveCount(1);
        var result = cells[0].Result;
        result.TradeCount.Should().Be(2);
        result.TradeRValues.Should().BeEquivalentTo(new[] { 2.0, -1.0 });
        result.AvgR.Should().BeApproximately(0.5, 0.01);
        result.WinRate.Should().BeApproximately(0.5, 0.01);
        result.MedianR.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void Evaluate_MultiCell_Parallel_RunsCorrectly()
    {
        var rules = ExitGridEvaluator.GenerateGrid(
            referenceAtrPips: 20.0,
            slMultiples: [1.5, 2.0],
            tpMultiples: [null, 2.0],
            beTriggers: [null],
            trailMultiples: [null]);

        var cells = ExitGridEvaluator.Evaluate(TwoLongTrades(), rules.ToList());

        cells.Should().HaveCount(4); // 2 × 2 × 1 × 1
        foreach (var cell in cells)
        {
            cell.Result.TradeCount.Should().Be(2);
            cell.Result.TradeRValues.Should().HaveCount(2);
        }
    }

    [Fact]
    public void No_TP_Rule_Produces_SL_Or_EndOfData()
    {
        var rules = new List<ExitRule>
        {
            new() { SlAtrMultiple = 1.5, TpRrMultiple = null, ReferenceAtrPips = 20.0 },
        };

        var cells = ExitGridEvaluator.Evaluate(TwoLongTrades(), rules);

        cells.Should().HaveCount(1);
        var rValues = cells[0].Result.TradeRValues;
        rValues.All(r => r <= 0).Should().BeTrue("no TP means all trades exit at SL or end-of-data");
    }
}
