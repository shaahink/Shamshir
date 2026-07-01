using TradingEngine.Host;

namespace TradingEngine.Tests.Unit.Kernel;

/// <summary>
/// Pure tests for the prop-firm reset-boundary detector (iter-36 K-GAP-1). The boundary math is the
/// sensitive part — get it wrong and every multi-day backtest silently mis-resets — so it is tested in
/// isolation, exhaustively, with no engine/IO. Weekday assumptions are guarded with BCL <c>DayOfWeek</c>
/// asserts so the fixture dates can't rot.
/// </summary>
[Trait("Category", "Kernel")]
[Trait("Speed", "Fast")]
public sealed class ResetClockTests
{
    private static readonly ResetConfig Utc22 = ResetConfig.FromRuleSet("22:00:00", "UTC");
    private static readonly ResetConfig UtcMidnight = ResetConfig.FromRuleSet("00:00:00", "UTC");

    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstBar_NoPrevious_CrossesNothing()
    {
        var rolls = ResetClock.Crossed(prevSimUtc: null, Utc(2026, 1, 7, 0), Utc22);
        rolls.Any.Should().BeFalse("the run's initial state IS the first period's baseline");
    }

    [Fact]
    public void WithinSameResetPeriod_NoRoll()
    {
        // Both 12:00 and 21:00 (before the 22:00 reset) belong to the same reset period → nothing crosses.
        var rolls = ResetClock.Crossed(Utc(2026, 1, 7, 12), Utc(2026, 1, 7, 21), Utc22);
        rolls.Any.Should().BeFalse();
    }

    [Fact]
    public void CrossesDailyResetTime_MidWeek_DayOnly()
    {
        new DateTime(2026, 1, 5).DayOfWeek.Should().Be(DayOfWeek.Monday, "fixture-date guard");
        new DateTime(2026, 1, 7).DayOfWeek.Should().Be(DayOfWeek.Wednesday, "fixture-date guard");

        // 21:00 (period Tue) → 22:00 (period Wed) on a mid-week Wednesday: a new day, same week, same month.
        var rolls = ResetClock.Crossed(Utc(2026, 1, 7, 21), Utc(2026, 1, 7, 22), Utc22);
        rolls.Day.Should().BeTrue();
        rolls.Week.Should().BeFalse();
        rolls.Month.Should().BeFalse();
    }

    [Fact]
    public void ResetBoundary_IsInclusive_BarExactlyAtResetTimeRolls()
    {
        var rolls = ResetClock.Crossed(Utc(2026, 1, 7, 21, 30), Utc(2026, 1, 7, 22, 0), Utc22);
        rolls.Day.Should().BeTrue("a bar opening exactly at the reset time starts the new period");
    }

    [Fact]
    public void CrossesIntoMonday_DayAndWeek()
    {
        new DateTime(2026, 1, 4).DayOfWeek.Should().Be(DayOfWeek.Sunday, "fixture-date guard");
        new DateTime(2026, 1, 5).DayOfWeek.Should().Be(DayOfWeek.Monday, "fixture-date guard");

        // Midnight reset → calendar-date periods. Sun 23:00 → Mon 00:00 = new day + new week, same month.
        var rolls = ResetClock.Crossed(Utc(2026, 1, 4, 23), Utc(2026, 1, 5, 0), UtcMidnight);
        rolls.Day.Should().BeTrue();
        rolls.Week.Should().BeTrue();
        rolls.Month.Should().BeFalse();
    }

    [Fact]
    public void CrossesIntoFirstOfMonth_DayAndMonth_NotWeek()
    {
        new DateTime(2026, 3, 1).DayOfWeek.Should().Be(DayOfWeek.Sunday, "fixture-date guard");

        // Feb 28 23:00 → Mar 1 00:00. Mar 1 2026 is a Sunday whose Monday-anchor (Feb 23) equals Feb 28's,
        // so it's a new day + new month but NOT a new week.
        var rolls = ResetClock.Crossed(Utc(2026, 2, 28, 23), Utc(2026, 3, 1, 0), UtcMidnight);
        rolls.Day.Should().BeTrue();
        rolls.Month.Should().BeTrue();
        rolls.Week.Should().BeFalse();
    }

    [Fact]
    public void WeekendGap_CollapsesToOneCrossingPerKind()
    {
        new DateTime(2026, 1, 9).DayOfWeek.Should().Be(DayOfWeek.Friday, "fixture-date guard");
        new DateTime(2026, 1, 12).DayOfWeek.Should().Be(DayOfWeek.Monday, "fixture-date guard");

        // Fri 20:00 → Mon 08:00 skips Sat+Sun+the Mon boundary, but reports a single day + single week roll
        // (the re-base uses current equity and the governor reset is idempotent, so repeats would be no-ops).
        var rolls = ResetClock.Crossed(Utc(2026, 1, 9, 20), Utc(2026, 1, 12, 8), UtcMidnight);
        rolls.Day.Should().BeTrue();
        rolls.Week.Should().BeTrue();
    }

    [Fact]
    public void CurrentBeforeOrEqualPrevious_NoRoll()
    {
        ResetClock.Crossed(Utc(2026, 1, 7, 22), Utc(2026, 1, 7, 22), Utc22).Any.Should().BeFalse();
        ResetClock.Crossed(Utc(2026, 1, 7, 22), Utc(2026, 1, 7, 21), Utc22).Any.Should().BeFalse();
    }

    [Fact]
    public void UnknownTimezone_FallsBackToUtc_DoesNotThrow()
    {
        var bogus = ResetConfig.FromRuleSet("00:00:00", "Mars/Olympus");
        var act = () => ResetClock.Crossed(Utc(2026, 1, 4, 23), Utc(2026, 1, 5, 0), bogus);

        act.Should().NotThrow();
        // Behaves as UTC: the same Sun→Mon midnight crossing still rolls the day.
        act().Day.Should().BeTrue();
    }
}
