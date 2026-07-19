using TradingEngine.Strategies.NyOpenDrive;
using TradingEngine.Tests.Unit.Strategies;

namespace TradingEngine.Tests.Unit.Strategies.NyOpenDrive;

[Trait("Category", "Strategy")]
public sealed class NyOpenDriveStrategyTests
{
    private const double Atr = 0.0010;

    private static NyOpenDriveStrategy Make(string mode = "drive") =>
        new(new NyOpenDriveConfig("ny-open-drive", "NY Open Drive", true, "standard",
                new NyOpenDriveParameters { Mode = mode }),
            SessionStrategyTestHelper.Registry(),
            Substitute.For<ILogger<NyOpenDriveStrategy>>());

    // M15 day (2024-06-03) 08:00→16:00 inclusive (enough warmup for RequiredBarCount = 19). The 13:30 bar
    // carries the opening drive (driveOpen → driveClose); every other bar is flat at 1.1000.
    private static List<Bar> BuildDay(decimal driveOpen, decimal driveClose)
    {
        var bars = new List<Bar>();
        var start = new DateTime(2024, 6, 3, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i <= 32; i++)
        {
            var open = start.AddMinutes(15 * i);
            if (TimeOnly.FromDateTime(open) == new TimeOnly(13, 30))
            {
                var hi = Math.Max(driveOpen, driveClose) + 0.0005m;
                var lo = Math.Min(driveOpen, driveClose) - 0.0005m;
                bars.Add(SessionStrategyTestHelper.M15(open, driveOpen, hi, lo, driveClose));
            }
            else
            {
                bars.Add(SessionStrategyTestHelper.Flat(open, 1.1000m));
            }
        }
        return bars;
    }

    private static int Idx(int hour, int minute) => (hour - 8) * 4 + minute / 15;

    [Fact]
    public void Up_drive_after_ny_open_goes_long()
    {
        var ctx = SessionStrategyTestHelper.Context(BuildDay(1.1000m, 1.1010m), Idx(13, 30), 1.1010m, Atr);

        var intent = Make().Evaluate(ctx);

        intent.Should().NotBeNull();
        intent!.Direction.Should().Be(TradeDirection.Long);
        intent.TakeProfit.Should().NotBeNull();
    }

    [Fact]
    public void Down_drive_after_ny_open_goes_short()
    {
        var ctx = SessionStrategyTestHelper.Context(BuildDay(1.1000m, 1.0990m), Idx(13, 30), 1.0990m, Atr);

        Make().Evaluate(ctx)!.Direction.Should().Be(TradeDirection.Short);
    }

    [Fact]
    public void Fade_mode_inverts_both_directions()
    {
        var up = SessionStrategyTestHelper.Context(BuildDay(1.1000m, 1.1010m), Idx(13, 30), 1.1010m, Atr);
        var down = SessionStrategyTestHelper.Context(BuildDay(1.1000m, 1.0990m), Idx(13, 30), 1.0990m, Atr);

        Make("fade").Evaluate(up)!.Direction.Should().Be(TradeDirection.Short, "fade inverts an up-drive to a short");
        Make("fade").Evaluate(down)!.Direction.Should().Be(TradeDirection.Long, "fade inverts a down-drive to a long");
    }

    [Fact]
    public void No_intent_outside_the_signal_window()
    {
        var day = BuildDay(1.1000m, 1.1010m);

        Make().Evaluate(SessionStrategyTestHelper.Context(day, Idx(12, 0), 1.1010m, Atr))
            .Should().BeNull("12:00 is before the 13:30 signal window");
        Make().Evaluate(SessionStrategyTestHelper.Context(day, Idx(15, 0), 1.1010m, Atr))
            .Should().BeNull("15:00 is the exclusive end of the signal window");
    }

    [Fact]
    public void Only_one_entry_per_day()
    {
        var day = BuildDay(1.1000m, 1.1010m);
        var strategy = Make();

        strategy.Evaluate(SessionStrategyTestHelper.Context(day, Idx(13, 30), 1.1010m, Atr))
            .Should().NotBeNull("the first in-window bar takes the drive");
        strategy.Evaluate(SessionStrategyTestHelper.Context(day, Idx(13, 45), 1.1010m, Atr))
            .Should().BeNull("a second bar the same day must not re-enter");
    }
}
