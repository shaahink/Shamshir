using TradingEngine.Strategies.AsiaRange;
using TradingEngine.Strategies.Sessions;
using TradingEngine.Tests.Unit.Strategies;

namespace TradingEngine.Tests.Unit.Strategies.AsiaRange;

[Trait("Category", "Strategy")]
public sealed class AsiaRangeStrategyTests
{
    private const decimal RangeHigh = 1.1020m;
    private const decimal RangeLow = 1.0980m;
    private const double Atr = 0.0010;

    private static AsiaRangeStrategy Make() =>
        new(new AsiaRangeConfig("asia-range", "Asia Range", true, "standard", new AsiaRangeParameters()),
            SessionStrategyTestHelper.Registry(),
            Substitute.For<ILogger<AsiaRangeStrategy>>());

    // M15 day (2024-06-03) 00:00→10:00 inclusive (41 bars). The 00:00-05:45 build-window bars define the
    // range [1.0980, 1.1020]; a 06:30 bar in the 06:00-07:00 GAP carries a huge range that MUST be ignored
    // (it is outside the build window); every other bar is flat at 1.1000.
    private static List<Bar> BuildDay()
    {
        var bars = new List<Bar>();
        var start = new DateTime(2024, 6, 3, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i <= 40; i++)
        {
            var open = start.AddMinutes(15 * i);
            var tod = TimeOnly.FromDateTime(open);
            if (tod >= new TimeOnly(0, 0) && tod < new TimeOnly(6, 0))
                bars.Add(SessionStrategyTestHelper.M15(open, 1.1000m, RangeHigh, RangeLow, 1.1000m));
            else if (tod == new TimeOnly(6, 30))
                bars.Add(SessionStrategyTestHelper.M15(open, 1.1000m, 1.1500m, 1.0500m, 1.1000m)); // gap trap
            else
                bars.Add(SessionStrategyTestHelper.Flat(open, 1.1000m));
        }
        return bars;
    }

    private static int Idx(int hour, int minute) => hour * 4 + minute / 15;

    [Fact]
    public void Breakout_above_overnight_range_high_goes_long()
    {
        var ctx = SessionStrategyTestHelper.Context(BuildDay(), Idx(7, 15), midPrice: 1.1030m, atr: Atr);

        var intent = Make().Evaluate(ctx);

        intent.Should().NotBeNull();
        intent!.Direction.Should().Be(TradeDirection.Long);
        intent.TakeProfit.Should().NotBeNull();
    }

    [Fact]
    public void Breakout_below_overnight_range_low_goes_short()
    {
        var ctx = SessionStrategyTestHelper.Context(BuildDay(), Idx(7, 15), midPrice: 1.0970m, atr: Atr);

        Make().Evaluate(ctx)!.Direction.Should().Be(TradeDirection.Short);
    }

    [Fact]
    public void No_intent_inside_range_or_in_the_gap_or_after_entry_window()
    {
        var day = BuildDay();

        Make().Evaluate(SessionStrategyTestHelper.Context(day, Idx(7, 15), 1.1000m, Atr))
            .Should().BeNull("price inside the range is not a breakout");
        Make().Evaluate(SessionStrategyTestHelper.Context(day, Idx(6, 30), 1.1030m, Atr))
            .Should().BeNull("06:30 is in the 06:00-07:00 gap, before the entry window opens");
        Make().Evaluate(SessionStrategyTestHelper.Context(day, Idx(10, 0), 1.1030m, Atr))
            .Should().BeNull("10:00 is the exclusive end of the entry window");
    }

    // Phase-3 requirement: exercise the overnight-wrap path of SessionWindow through OpeningRangeTracker,
    // even though the shipped asia-range window (00:00-06:00) does not actually wrap midnight. A 22:00-06:00
    // window on one calendar day includes both the post-midnight leg (02:00) and the pre-midnight leg
    // (23:00), and excludes midday (12:00).
    [Fact]
    public void OpeningRangeTracker_handles_a_wrapping_window()
    {
        var tracker = new OpeningRangeTracker(new SessionWindow(new TimeOnly(22, 0), new TimeOnly(6, 0)));
        var day = new DateTime(2024, 6, 3, 0, 0, 0, DateTimeKind.Utc);
        var bars = new List<Bar>
        {
            SessionStrategyTestHelper.M15(day.AddHours(2), 1.1000m, 1.1030m, 1.1000m, 1.1010m),  // 02:00 in
            SessionStrategyTestHelper.M15(day.AddHours(12), 1.1000m, 1.5000m, 0.5000m, 1.1000m), // 12:00 out (trap)
            SessionStrategyTestHelper.M15(day.AddHours(23), 1.1000m, 1.1040m, 1.0990m, 1.1000m), // 23:00 in
        };

        var range = tracker.Compute(bars, day.AddHours(23));

        range.Should().NotBeNull();
        range!.Value.High.Should().Be(1.1040m, "the 23:00 pre-midnight-leg bar is inside the wrap window");
        range.Value.Low.Should().Be(1.0990m, "midday's huge range is excluded");
    }
}
