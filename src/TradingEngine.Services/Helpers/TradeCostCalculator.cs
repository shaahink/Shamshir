namespace TradingEngine.Services.Helpers;

/// <summary>
/// Itemised result of closing a position: the gross (cost-free) PnL, the round-turn commission,
/// the accrued overnight swap, and the resulting net. All costs follow the unified negative
/// convention (D9): costs are NEGATIVE, <c>Net = Gross + Commission + Swap</c>.
/// <see cref="NightsHeld"/> is surfaced for the journal so a reader can see WHY the swap is what it is.
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
/// <see cref="PipCalculator.GrossPnL"/>.
///
/// <para>Costs follow the cTrader/industry convention (D9): <b>costs are NEGATIVE</b>,
/// <c>Net = Gross + Commission + Swap</c>. Commission is always a cost (negative); swap is
/// negated from the broker rate so that a cost-rate produces a negative number.</para>
///
/// <para>Commission uses the per-lot-per-side rate from <see cref="SymbolInfo"/>. The
/// <paramref name="commissionPerMillion"/> parameter is reserved for P1 (venue-declared commission
/// model) — when <c>null</c> the symbol-level rate is used.</para>
///
/// <para>Swap sources its rate from <see cref="SymbolInfo"/> swap rates.
/// <paramref name="swapLongPerLotPerNight"/> / <paramref name="swapShortPerLotPerNight"/> overrides
/// are reserved for P1 (venue-declared rates).</para>
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
        TimeSpan? dailyResetUtc = null,
        decimal? commissionPerMillion = null,
        decimal? swapLongPerLotPerNight = null,
        decimal? swapShortPerLotPerNight = null)
    {
        var gross = PipCalculator.GrossPnL(direction, entryPrice, exitPrice, lots, symbol, getCrossRate).Amount;

        var commission = -(lots * symbol.CommissionPerLotPerSide * 2m);

        var nights = CountNightsHeld(openedAtUtc, closedAtUtc, symbol.TripleSwapWeekday,
            dailyResetUtc ?? DefaultDailyResetUtc);
        var swapRate = direction == TradeDirection.Long
            ? swapLongPerLotPerNight ?? symbol.SwapLongPerLotPerNight
            : swapShortPerLotPerNight ?? symbol.SwapShortPerLotPerNight;
        var swap = -(nights * swapRate * lots);

        var net = gross + commission + swap;
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

        var d = openedUtc.Date;
        var resetTime = d + dailyResetUtc;
        if (openedUtc > resetTime) d = d.AddDays(1);

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
