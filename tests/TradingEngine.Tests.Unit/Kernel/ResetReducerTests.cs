using TradingEngine.Engine;

namespace TradingEngine.Tests.Unit.Kernel;

/// <summary>
/// Guards the iter-36 K-GAP-1 reducer fix: a day/week/month roll must re-base drawdown to the AUTHORITATIVE
/// current equity (<c>EngineState.Account.Equity</c>), not the stale previous start equity, and a day roll
/// must reset the governor (H7). Before the fix the re-base passed the old start back to itself, so a
/// multi-day run's daily/weekly/monthly drawdown never actually moved off the run's first baseline.
/// Pure reducer tests — no kernel config, no IO.
/// </summary>
[Trait("Category", "Kernel")]
[Trait("Speed", "Fast")]
public sealed class ResetReducerTests
{
    private static readonly DateTime Roll = new(2026, 1, 7, 22, 0, 0, DateTimeKind.Utc);

    // Day started at 10,000; equity has since dropped to 9,500 (a 5% intraday drawdown). The reset should
    // re-base every period's start to the current 9,500 — the new period opens flat.
    private static EngineState DrawnDownState()
    {
        var dd = DrawdownReducer.Apply(DrawdownReducer.CreateInitial(10_000m, "Fixed"), 9_500m);
        return EngineState.Empty with
        {
            Drawdown = dd,
            Account = new AccountView(9_500m, 9_500m, 0m),
        };
    }

    [Fact]
    public void DayRolled_RebasesDailyStartToCurrentEquity_NotStaleStart()
    {
        var before = DrawnDownState();
        before.Drawdown.DailyStartEquity.Should().Be(10_000m, "precondition: the day opened at 10,000");

        var after = EngineReducer.Apply(before, new DayRolled(Roll)).State;

        after.Drawdown.DailyStartEquity.Should().Be(9_500m, "the day re-bases to the authoritative current equity");
        after.Drawdown.CurrentDailyDrawdown.Should().Be(0m, "the new day opens flat");
    }

    [Fact]
    public void DayRolled_ResetsGovernorProfitLock()
    {
        var state = DrawnDownState() with
        {
            Governor = GovernorMachine.CreateInitial() with
            {
                State = GovernorTradingState.ProfitLocked,
                ProfitLockedToday = true,
            },
        };

        var after = EngineReducer.Apply(state, new DayRolled(Roll)).State;

        after.Governor.State.Should().Be(GovernorTradingState.Normal, "the day reset clears a profit lock (H7)");
        after.Governor.ProfitLockedToday.Should().BeFalse();
    }

    [Fact]
    public void WeekRolled_RebasesWeeklyStartToCurrentEquity()
    {
        var after = EngineReducer.Apply(DrawnDownState(), new WeekRolled(Roll)).State;

        after.Drawdown.WeeklyStartEquity.Should().Be(9_500m);
        after.Drawdown.CurrentWeeklyDrawdown.Should().Be(0m);
    }

    [Fact]
    public void MonthRolled_RebasesMonthlyStartToCurrentEquity()
    {
        var after = EngineReducer.Apply(DrawnDownState(), new MonthRolled(Roll)).State;

        after.Drawdown.MonthlyStartEquity.Should().Be(9_500m);
        after.Drawdown.CurrentMonthlyDrawdown.Should().Be(0m);
    }
}
