namespace TradingEngine.Services.Helpers;

/// <summary>
/// Itemised result of closing a position: the gross (cost-free) PnL, the round-turn commission,
/// the accrued overnight swap, and the resulting net. <see cref="NightsHeld"/> is surfaced for the
/// journal so a reader can see WHY the swap is what it is.
/// </summary>
public readonly record struct TradeCosts(
    decimal GrossProfit,
    decimal Commission,
    decimal Swap,
    decimal NetProfit,
    int NightsHeld);

/// <summary>
/// Single source of truth for trade-close economics, shared by every venue so the simulated and the
/// replay backtests (and the live itemisation) can never diverge. Gross PnL uses the canonical
/// <see cref="PipCalculator.GrossPnL"/> (which correctly handles account-currency, base==account and
/// cross-quoted symbols — the previous inline formula in the simulated venue mis-priced USD-base pairs
/// such as USDJPY/USDCHF/USDCAD). Commission is a round-turn charge (per-side × 2). Swap accrues per
/// rollover boundary crossed, tripled on the configured triple-swap weekday.
/// </summary>
public static class TradeCostCalculator
{
    public static readonly TimeSpan DefaultDailyResetUtc = TimeSpan.FromHours(22);

    public static TradeCosts Compute(
        TradeDirection direction,
        Price entryPrice,
        Price exitPrice,
        decimal lots,
        SymbolInfo symbol,
        Func<string, string, decimal> getCrossRate,
        DateTime openedAtUtc,
        DateTime closedAtUtc,
        TimeSpan? dailyResetUtc = null)
    {
        var gross = PipCalculator.GrossPnL(direction, entryPrice, exitPrice, lots, symbol, getCrossRate).Amount;

        var commission = lots * symbol.CommissionPerLotPerSide * 2m;

        var nights = CountNightsHeld(openedAtUtc, closedAtUtc, symbol.TripleSwapWeekday,
            dailyResetUtc ?? DefaultDailyResetUtc);
        var swapRate = direction == TradeDirection.Long
            ? symbol.SwapLongPerLotPerNight
            : symbol.SwapShortPerLotPerNight;
        var swap = nights * swapRate * lots;

        var net = gross - commission - swap;
        return new TradeCosts(gross, commission, swap, net, nights);
    }

    /// <summary>
    /// Counts the number of daily rollover boundaries (default 22:00 UTC) strictly crossed between
    /// open and close, charging triple on the configured triple-swap weekday. A trade opened and
    /// closed without crossing a rollover holds zero nights.
    /// </summary>
    public static int CountNightsHeld(
        DateTime openedUtc, DateTime closedUtc, string tripleSwapWeekday, TimeSpan dailyResetUtc)
    {
        if (openedUtc >= closedUtc) return 0;

        // First rollover on/after open: the reset on the open's date, or the next day's if open is
        // already past today's reset.
        var d = openedUtc.Date;
        var resetTime = d + dailyResetUtc;
        if (openedUtc > resetTime) d = d.AddDays(1);

        // Last rollover on/before close: the reset on the close's date, or the prior day's if close
        // hasn't reached today's reset yet.
        var end = closedUtc.Date;
        if (closedUtc < end + dailyResetUtc) end = end.AddDays(-1);

        var triple = Enum.TryParse<DayOfWeek>(tripleSwapWeekday, ignoreCase: true, out var tw)
            ? tw
            : DayOfWeek.Wednesday;

        var count = 0;
        for (var day = d; day <= end; day = day.AddDays(1))
            count += day.DayOfWeek == triple ? 3 : 1;
        return count;
    }
}
