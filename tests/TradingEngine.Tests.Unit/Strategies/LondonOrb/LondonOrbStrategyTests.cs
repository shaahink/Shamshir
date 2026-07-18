using TradingEngine.Strategies.LondonOrb;
using TradingEngine.Tests.Unit.Strategies;

namespace TradingEngine.Tests.Unit.Strategies.LondonOrb;

[Trait("Category", "Strategy")]
public sealed class LondonOrbStrategyTests
{
    private const decimal RangeHigh = 1.1020m;
    private const decimal RangeLow = 1.0980m;
    private const double Atr = 0.0010;

    private static LondonOrbStrategy Make() =>
        new(new LondonOrbConfig("london-orb", "London ORB", true, "standard", new LondonOrbParameters()),
            SessionStrategyTestHelper.Registry(),
            Substitute.For<ILogger<LondonOrbStrategy>>());

    // A full M15 day (2024-06-03, Monday) 03:00→11:00 inclusive (33 bars, enough warmup for RequiredBarCount
    // = AtrPeriod + 5 = 19). The 07:00-07:45 build-window bars define the range [1.0980, 1.1020]; a 06:00
    // out-of-window bar carries a huge range that MUST be ignored; every other bar is flat at 1.1000.
    private static List<Bar> BuildDay()
    {
        var bars = new List<Bar>();
        var start = new DateTime(2024, 6, 3, 3, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i <= 32; i++)
        {
            var open = start.AddMinutes(15 * i);
            var tod = TimeOnly.FromDateTime(open);
            if (tod >= new TimeOnly(7, 0) && tod < new TimeOnly(8, 0))
                bars.Add(SessionStrategyTestHelper.M15(open, 1.1000m, RangeHigh, RangeLow, 1.1000m));
            else if (tod == new TimeOnly(6, 0))
                bars.Add(SessionStrategyTestHelper.M15(open, 1.1000m, 1.1500m, 1.0500m, 1.1000m)); // out-of-window trap
            else
                bars.Add(SessionStrategyTestHelper.Flat(open, 1.1000m));
        }
        return bars;
    }

    private static int Idx(int hour, int minute) => (hour - 3) * 4 + minute / 15;

    [Fact]
    public void Breakout_above_range_high_in_window_goes_long_with_sl_tp()
    {
        var bars = BuildDay();
        var ctx = SessionStrategyTestHelper.Context(bars, Idx(8, 15), midPrice: 1.1030m, atr: Atr);

        var intent = Make().Evaluate(ctx);

        intent.Should().NotBeNull("a break above the in-window range high inside the entry window is a long");
        intent!.Direction.Should().Be(TradeDirection.Long);
        intent.TakeProfit.Should().NotBeNull();
        intent.StopLoss.Value.Should().BeLessThan(1.1030m, "a long's stop sits below entry");
    }

    [Fact]
    public void Breakout_below_range_low_in_window_goes_short()
    {
        var bars = BuildDay();
        var ctx = SessionStrategyTestHelper.Context(bars, Idx(8, 15), midPrice: 1.0970m, atr: Atr);

        var intent = Make().Evaluate(ctx);

        intent.Should().NotBeNull();
        intent!.Direction.Should().Be(TradeDirection.Short);
        intent.StopLoss.Value.Should().BeGreaterThan(1.0970m, "a short's stop sits above entry");
    }

    [Fact]
    public void No_intent_when_price_stays_inside_the_range()
    {
        var bars = BuildDay();
        var ctx = SessionStrategyTestHelper.Context(bars, Idx(8, 15), midPrice: 1.1000m, atr: Atr);

        Make().Evaluate(ctx).Should().BeNull();
    }

    [Fact]
    public void No_intent_before_entry_window_opens()
    {
        var bars = BuildDay();
        var ctx = SessionStrategyTestHelper.Context(bars, Idx(7, 30), midPrice: 1.1030m, atr: Atr);

        Make().Evaluate(ctx).Should().BeNull("07:30 is still inside the range-build window, before entry");
    }

    [Fact]
    public void No_intent_at_or_after_entry_window_end()
    {
        var bars = BuildDay();
        var ctx = SessionStrategyTestHelper.Context(bars, Idx(11, 0), midPrice: 1.1030m, atr: Atr);

        Make().Evaluate(ctx).Should().BeNull("11:00 is the exclusive end of the entry window");
    }
}
