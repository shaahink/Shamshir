namespace TradingEngine.Services.Strategy.Filters;

public sealed class SessionTimeFilter(TimeOnly openUtc, TimeOnly closeUtc, bool excludeWeekends = true) : IEntryFilter
{
    public bool Allows(MarketContext ctx)
    {
        var t = TimeOnly.FromDateTime(ctx.EngineTimeUtc);
        if (excludeWeekends && ctx.EngineTimeUtc.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;
        return t >= openUtc && t < closeUtc;
    }
}
