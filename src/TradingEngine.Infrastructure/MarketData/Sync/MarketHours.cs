using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.MarketData.Sync;

/// <summary>
/// X4 — the small amount of market-calendar truth the coverage/sync logic needs so a normal weekend
/// closure is not mistaken for "stale" or a "gap". FX closes ~Friday 21:00 UTC and reopens ~Sunday
/// 21:00 UTC (approximate, DST-agnostic — good enough to avoid false weekend alarms). Crypto is 24/7.
/// </summary>
public static class MarketHours
{
    private const int FridayCloseHourUtc = 21;
    private const int SundayOpenHourUtc = 21;

    /// <summary>24/7 instruments (crypto) never have a weekend closure.</summary>
    public static bool Is247(string symbol)
    {
        var s = symbol.ToUpperInvariant();
        return s.Contains("BTC") || s.Contains("ETH") || s.Contains("LTC")
            || s.Contains("XRP") || s.Contains("DOGE") || s.Contains("SOL");
    }

    /// <summary>True when <paramref name="instantUtc"/> falls inside the FX weekend closure.</summary>
    public static bool IsForexClosed(DateTime instantUtc)
    {
        var t = DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc);
        return t.DayOfWeek switch
        {
            DayOfWeek.Saturday => true,
            DayOfWeek.Friday => t.Hour >= FridayCloseHourUtc,
            DayOfWeek.Sunday => t.Hour < SundayOpenHourUtc,
            _ => false,
        };
    }

    /// <summary>
    /// The most recent instant the market should have produced a bar as of <paramref name="nowUtc"/>.
    /// For FX during a weekend closure this rolls back to the Friday close; otherwise it is "now". Used to
    /// decide staleness without flagging weekends.
    /// </summary>
    public static DateTime ExpectedLatestUtc(string symbol, DateTime nowUtc)
    {
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        if (Is247(symbol) || !IsForexClosed(now))
        {
            return now;
        }
        // Roll back to the most recent Friday 21:00 UTC.
        var t = now;
        while (t.DayOfWeek != DayOfWeek.Friday)
        {
            t = t.AddDays(-1);
        }
        return new DateTime(t.Year, t.Month, t.Day, FridayCloseHourUtc, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>Bar length for a timeframe (used to size the staleness tolerance).</summary>
    public static TimeSpan Interval(Timeframe tf) => tf switch
    {
        Timeframe.M1 => TimeSpan.FromMinutes(1),
        Timeframe.M5 => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.M30 => TimeSpan.FromMinutes(30),
        Timeframe.H1 => TimeSpan.FromHours(1),
        Timeframe.H4 => TimeSpan.FromHours(4),
        Timeframe.D1 => TimeSpan.FromDays(1),
        _ => TimeSpan.FromHours(1),
    };
}
