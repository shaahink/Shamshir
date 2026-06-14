namespace TradingEngine.Engine;

public static class DrawdownReducer
{
    public static DrawdownState Apply(DrawdownState state, decimal equity)
    {
        var peakEquity = equity > state.PeakEquity ? equity : state.PeakEquity;
        var dailyStartEquity = state.DailyStartEquity;

        var currentDailyDrawdown = dailyStartEquity > 0
            ? Math.Max(0m, (dailyStartEquity - equity) / dailyStartEquity)
            : 0m;

        var currentWeeklyDrawdown = state.WeeklyStartEquity > 0
            ? Math.Max(0m, (state.WeeklyStartEquity - equity) / state.WeeklyStartEquity)
            : 0m;

        var currentMonthlyDrawdown = state.MonthlyStartEquity > 0
            ? Math.Max(0m, (state.MonthlyStartEquity - equity) / state.MonthlyStartEquity)
            : 0m;

        var equityBase = state.DrawdownType == "Trailing" ? peakEquity : state.InitialAccountBalance;
        var currentMaxDrawdown = equityBase > 0
            ? Math.Max(0m, (equityBase - equity) / equityBase)
            : 0m;

        return state with
        {
            PeakEquity = peakEquity,
            CurrentDailyDrawdown = currentDailyDrawdown,
            CurrentMaxDrawdown = currentMaxDrawdown,
            CurrentWeeklyDrawdown = currentWeeklyDrawdown,
            CurrentMonthlyDrawdown = currentMonthlyDrawdown,
        };
    }

    public static DrawdownState CreateInitial(decimal initialBalance, string drawdownType = "Fixed")
    {
        return new DrawdownState(
            initialBalance,
            initialBalance,
            initialBalance,
            initialBalance,
            initialBalance,
            0,
            0,
            0,
            0,
            0,
            drawdownType);
    }

    public static DrawdownState ApplyDailyReset(DrawdownState state, decimal currentEquity)
    {
        return state with
        {
            DailyStartEquity = currentEquity,
            CurrentDailyDrawdown = 0,
        };
    }

    public static DrawdownState ApplyWeeklyReset(DrawdownState state, decimal currentEquity)
    {
        return state with
        {
            WeeklyStartEquity = currentEquity,
            CurrentWeeklyDrawdown = 0,
        };
    }

    public static DrawdownState ApplyMonthlyReset(DrawdownState state, decimal currentEquity)
    {
        return state with
        {
            MonthlyStartEquity = currentEquity,
            CurrentMonthlyDrawdown = 0,
        };
    }
}
