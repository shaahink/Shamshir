namespace TradingEngine.Engine;

public static class DrawdownReducer
{
    public static DrawdownState Apply(DrawdownState state, decimal equity)
    {
        if (!state.IsInitialized) return state;

        var peakEquity = equity > state.PeakEquity ? equity : state.PeakEquity;

        // F79: DailyDdBaseMode selects the ALLOWANCE BASE (what the 5% is a percentage of), never the
        // anchor — the day's loss is always measured from DailyStartEquity. The old InitialBalance arm
        // anchored at initial too, turning "daily" DD into cumulative DD: any account sitting below
        // initial re-breached the daily limit every day forever (verified FTMO semantics: floor =
        // prev-midnight balance − 5% × initial; V0 rule-diff rows 4–5).
        var currentDailyDrawdown = state.DailyDdBaseMode == "DailyStart"
            ? state.DailyStartEquity > 0
                ? Math.Max(0m, (state.DailyStartEquity - equity) / state.DailyStartEquity)
                : 0m
            : state.InitialAccountBalance > 0
                ? Math.Max(0m, (state.DailyStartEquity - equity) / state.InitialAccountBalance)
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
            drawdownType,
            "InitialBalance",
            true,
            []);
    }

    public static DrawdownState ApplyDailyReset(DrawdownState state, decimal currentEquity)
    {
        var window = new List<decimal>(state.VelocityWindow) { state.CurrentMaxDrawdown };
        while (window.Count > 5)
            window.RemoveAt(0);

        var drawdownVelocity = state.DrawdownVelocity;
        if (window.Count >= 2)
        {
            double sum = 0;
            for (int i = 1; i < window.Count; i++)
                sum += (double)(window[i] - window[i - 1]);
            drawdownVelocity = (decimal)(sum / (window.Count - 1));
        }

        return state with
        {
            DailyStartEquity = currentEquity,
            CurrentDailyDrawdown = 0,
            DrawdownVelocity = drawdownVelocity,
            VelocityWindow = window,
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
