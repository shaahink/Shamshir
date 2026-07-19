using TradingEngine.Strategies.Sessions;

namespace TradingEngine.Tests.Unit.Strategies.Sessions;

[Trait("Category", "Strategy")]
public sealed class SessionWindowTests
{
    private static DateTime At(int hour, int minute = 0) =>
        new(2024, 6, 3, hour, minute, 0, DateTimeKind.Utc); // 2024-06-03 is a Monday

    [Fact]
    public void NonWrapping_window_is_start_inclusive_end_exclusive()
    {
        var w = new SessionWindow(new TimeOnly(7, 0), new TimeOnly(8, 0));

        w.Contains(At(7, 0)).Should().BeTrue("start is inclusive");
        w.Contains(At(7, 30)).Should().BeTrue("07:30 is inside 07:00–08:00");
        w.Contains(At(8, 0)).Should().BeFalse("end is exclusive");
        w.Contains(At(6, 59)).Should().BeFalse("06:59 is before the window");
    }

    [Fact]
    public void Wrapping_window_spans_midnight()
    {
        var w = new SessionWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));

        w.Contains(At(23, 0)).Should().BeTrue("23:00 is inside the pre-midnight leg");
        w.Contains(At(2, 0)).Should().BeTrue("02:00 is inside the post-midnight leg");
        w.Contains(At(22, 0)).Should().BeTrue("start is inclusive");
        w.Contains(At(6, 0)).Should().BeFalse("end is exclusive");
        w.Contains(At(12, 0)).Should().BeFalse("midday is outside an overnight window");
    }
}
