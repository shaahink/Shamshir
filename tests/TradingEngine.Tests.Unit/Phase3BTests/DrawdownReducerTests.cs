using TradingEngine.Engine;

namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Engine")]
[Trait("Speed", "Fast")]
public sealed class DrawdownReducerTests
{
    [Fact]
    public void Apply_EquityIncreases_UpdatesPeak()
    {
        var state = DrawdownReducer.CreateInitial(100_000);
        var result = DrawdownReducer.Apply(state, 101_000);

        result.PeakEquity.Should().Be(101_000);
        result.CurrentDailyDrawdown.Should().Be(0);
        result.CurrentMaxDrawdown.Should().Be(0);
    }

    [Fact]
    public void Apply_EquityDecreases_ComputesDrawdown()
    {
        var state = DrawdownReducer.CreateInitial(100_000);
        var result = DrawdownReducer.Apply(state, 98_000);

        result.PeakEquity.Should().Be(100_000);
        result.CurrentDailyDrawdown.Should().Be(0.02m);
        result.CurrentMaxDrawdown.Should().Be(0.02m);
    }

    [Fact]
    public void Apply_TrailingDrawdown_UsesPeakNotInitial()
    {
        var state = DrawdownReducer.CreateInitial(100_000, "Trailing");
        state = DrawdownReducer.Apply(state, 110_000);
        var result = DrawdownReducer.Apply(state, 107_800);

        result.PeakEquity.Should().Be(110_000);
        result.CurrentMaxDrawdown.Should().Be(0.02m);
    }

    [Fact]
    public void Apply_DailyReset_SetsNewStartEquity()
    {
        var state = DrawdownReducer.CreateInitial(100_000);
        state = DrawdownReducer.Apply(state, 98_000);
        var result = DrawdownReducer.ApplyDailyReset(state, 98_000);

        result.DailyStartEquity.Should().Be(98_000);
        result.CurrentDailyDrawdown.Should().Be(0);
        result.CurrentMaxDrawdown.Should().Be(0.02m);
    }
}
