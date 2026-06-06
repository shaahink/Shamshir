namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Services")]
public sealed class SlTpCalculatorPhase3BTests
{
    [Fact]
    public void CalculateStopLoss_NoLongerThrows()
    {
        var calc = new SlTpCalculator();
        var bars = new List<Bar>
        {
            new(Symbol.Parse("EURUSD"), Timeframe.H1, DateTime.UtcNow, 1.0850m, 1.0860m, 1.0840m, 1.0855m, 1000),
        };
        var sl = calc.CalculateStopLoss(new Price(1.08500m), TradeDirection.Long,
            SlMethod.FixedPips, new SlParameters(new Pips(20), null, null, null), bars);
        sl.Value.Should().Be(1.08300m);
    }

    [Fact]
    public void CalculateTakeProfit_NoLongerThrows()
    {
        var calc = new SlTpCalculator();
        var tp = calc.CalculateTakeProfit(new Price(1.08500m), new Price(1.08300m),
            TradeDirection.Long, TpMethod.RRMultiple, new TpParameters(null, 2.0, null));
        tp.Should().NotBeNull();
        tp!.Value.Value.Should().Be(1.08900m);
    }
}
