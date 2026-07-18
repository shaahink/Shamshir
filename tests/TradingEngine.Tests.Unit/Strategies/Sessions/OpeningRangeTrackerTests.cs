using TradingEngine.Strategies.Sessions;

namespace TradingEngine.Tests.Unit.Strategies.Sessions;

[Trait("Category", "Strategy")]
public sealed class OpeningRangeTrackerTests
{
    private static readonly Symbol Eur = Symbol.Parse("EURUSD");

    private static Bar Bar15(DateTime open, decimal high, decimal low) =>
        new(Eur, Timeframe.M15, open, (high + low) / 2m, high, low, (high + low) / 2m, 1000);

    private static DateTime Day1(int hour, int minute = 0) =>
        new(2024, 6, 3, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void Builds_high_low_over_in_window_bars()
    {
        var tracker = new OpeningRangeTracker(new SessionWindow(new TimeOnly(7, 0), new TimeOnly(8, 0)));
        var bars = new List<Bar>
        {
            Bar15(Day1(7, 0), 1.1010m, 1.0990m),
            Bar15(Day1(7, 15), 1.1025m, 1.1000m), // highest high
            Bar15(Day1(7, 30), 1.1015m, 1.0980m), // lowest low
        };

        var range = tracker.Compute(bars, Day1(8, 0));

        range.Should().NotBeNull();
        range!.Value.High.Should().Be(1.1025m);
        range.Value.Low.Should().Be(1.0980m);
    }

    [Fact]
    public void Ignores_out_of_window_and_prior_day_bars()
    {
        var tracker = new OpeningRangeTracker(new SessionWindow(new TimeOnly(7, 0), new TimeOnly(8, 0)));
        var bars = new List<Bar>
        {
            Bar15(Day1(7, 15).AddDays(-1), 1.2000m, 0.9000m), // prior day — must be ignored
            Bar15(Day1(6, 45), 1.3000m, 0.8000m),             // before the window — ignored
            Bar15(Day1(7, 0), 1.1010m, 1.0990m),              // in window
            Bar15(Day1(7, 45), 1.1020m, 1.0985m),             // in window
            Bar15(Day1(8, 0), 1.4000m, 0.7000m),              // at/after end (exclusive) — ignored
        };

        var range = tracker.Compute(bars, Day1(8, 30));

        range.Should().NotBeNull();
        range!.Value.High.Should().Be(1.1020m, "only the two in-window bars count");
        range.Value.Low.Should().Be(1.0985m);
    }

    [Fact]
    public void Returns_null_when_no_in_window_bars()
    {
        var tracker = new OpeningRangeTracker(new SessionWindow(new TimeOnly(7, 0), new TimeOnly(8, 0)));
        var bars = new List<Bar> { Bar15(Day1(9, 0), 1.1010m, 1.0990m) };

        tracker.Compute(bars, Day1(9, 30)).Should().BeNull();
    }

    [Fact]
    public void Is_stateless_across_reset()
    {
        var tracker = new OpeningRangeTracker(new SessionWindow(new TimeOnly(7, 0), new TimeOnly(8, 0)));
        var bars = new List<Bar> { Bar15(Day1(7, 15), 1.1025m, 1.0980m) };

        tracker.Compute(bars, Day1(8, 0)).Should().NotBeNull();
        tracker.Reset();
        // The tracker recomputes purely from the bars it is handed — Reset changes nothing about the result.
        tracker.Compute(bars, Day1(8, 0)).Should().NotBeNull();
    }
}
