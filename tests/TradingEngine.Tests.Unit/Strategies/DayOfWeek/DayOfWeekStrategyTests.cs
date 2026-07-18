using TradingEngine.Strategies.DayOfWeek;
using TradingEngine.Tests.Unit.Strategies;

namespace TradingEngine.Tests.Unit.Strategies.DayOfWeek;

[Trait("Category", "Strategy")]
public sealed class DayOfWeekStrategyTests
{
    private const double Atr = 0.0010;

    private static DayOfWeekStrategy Make(string direction = "Long", params string[] weekdays) =>
        new(new DayOfWeekConfig("day-of-week", "Day-of-Week Bias", true, "standard",
                new DayOfWeekParameters
                {
                    Direction = direction,
                    Weekdays = weekdays.Length > 0 ? weekdays : ["Monday"],
                }),
            SessionStrategyTestHelper.Registry(),
            Substitute.For<ILogger<DayOfWeekStrategy>>());

    // A rolling M15 span from Sunday 2024-06-02 19:00 through Wednesday, all flat at 1.1000, so every
    // weekday's 00:00 bar has ≥ RequiredBarCount (19) of prior warmup.
    private static readonly List<Bar> Span = BuildSpan();

    private static List<Bar> BuildSpan()
    {
        var bars = new List<Bar>();
        var start = new DateTime(2024, 6, 2, 19, 0, 0, DateTimeKind.Utc); // Sunday
        for (var i = 0; i < 320; i++)
            bars.Add(SessionStrategyTestHelper.Flat(start.AddMinutes(15 * i), 1.1000m));
        return bars;
    }

    private static int IndexAt(DateTime open) => Span.FindIndex(b => b.OpenTimeUtc == open);

    private static MarketContext ContextAt(DateTime open) =>
        SessionStrategyTestHelper.Context(Span, IndexAt(open), 1.1000m, Atr);

    private static DateTime Monday(int h) => new(2024, 6, 3, h, 0, 0, DateTimeKind.Utc);
    private static DateTime Tuesday(int h) => new(2024, 6, 4, h, 0, 0, DateTimeKind.Utc);
    private static DateTime Wednesday(int h) => new(2024, 6, 5, h, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Fires_on_monday_at_entry_hour_with_sl_tp()
    {
        var intent = Make().Evaluate(ContextAt(Monday(0)));

        intent.Should().NotBeNull();
        intent!.Direction.Should().Be(TradeDirection.Long);
        intent.TakeProfit.Should().NotBeNull();
        intent.StopLoss.Value.Should().BeLessThan(1.1000m, "a long's stop sits below entry");
    }

    [Fact]
    public void Does_not_fire_on_a_non_listed_weekday()
    {
        Make().Evaluate(ContextAt(Tuesday(0))).Should().BeNull("Tuesday is not in the weekdays list");
    }

    [Fact]
    public void Does_not_fire_at_other_hours()
    {
        Make().Evaluate(ContextAt(Monday(5))).Should().BeNull("05:00 is not the exact entry hour");
    }

    [Fact]
    public void Short_direction_flips_the_side()
    {
        var intent = Make(direction: "Short").Evaluate(ContextAt(Monday(0)));

        intent.Should().NotBeNull();
        intent!.Direction.Should().Be(TradeDirection.Short);
        intent.StopLoss.Value.Should().BeGreaterThan(1.1000m, "a short's stop sits above entry");
    }

    [Fact]
    public void Multiple_weekdays_fire_on_each_listed_day_only()
    {
        var strategy = Make("Long", "Monday", "Wednesday");

        strategy.Evaluate(ContextAt(Monday(0))).Should().NotBeNull("Monday is listed");
        strategy.Evaluate(ContextAt(Tuesday(0))).Should().BeNull("Tuesday is not listed");
        strategy.Evaluate(ContextAt(Wednesday(0))).Should().NotBeNull("Wednesday is listed");
    }
}
