namespace TradingEngine.Risk.Filters;

public sealed class SessionFilter
{
    public bool IsInSession(DateTime utcNow, TimeSpan sessionOpen, TimeSpan sessionClose)
    {
        var timeOfDay = utcNow.TimeOfDay;

        if (sessionClose > sessionOpen)
            return timeOfDay >= sessionOpen && timeOfDay < sessionClose;

        return timeOfDay >= sessionOpen || timeOfDay < sessionClose;
    }

    public bool IsWeekend(DateTime utcNow)
    {
        return utcNow.DayOfWeek == DayOfWeek.Saturday
            || utcNow.DayOfWeek == DayOfWeek.Sunday;
    }
}
