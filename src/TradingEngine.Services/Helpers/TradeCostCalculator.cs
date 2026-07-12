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
/// <para>Commission model: dispatches on <see cref="SymbolInfo.CommissionType"/>.
/// <b>AbsolutePerLot</b>: rate is a flat per-lot-per-side fee. <b>UsdPerMillionUsdVolume</b>: rate
/// is USD charged per million USD of notional volume per side. The notional is
/// <c>lots × contractSize × baseToUsdRate</c>, where baseToUsdRate = price for USD-quoted symbols
/// and = 1 for USD-based symbols. Cross pairs (e.g. EURJPY) use the supplied cross-rate function.</para>
///
/// <para>Half-at-open (D10): <see cref="ComputeEntryCommission"/> returns the per-side commission
/// payable at position open. <see cref="Compute"/> returns the full round-trip. Adapters deduct the
/// entry side at open and the remaining close side at close, keeping intra-trade equity truthful.</para>
///
/// <para>Swap sources its rate from <see cref="SymbolInfo"/> swap rates.
/// <paramref name="swapLongPerLotPerNight"/> / <paramref name="swapShortPerLotPerNight"/> overrides
/// allow per-run calibration.</para>
/// </summary>
public static class TradeCostCalculator
{
    public static readonly TimeSpan DefaultDailyResetUtc = TimeSpan.FromHours(22);

    /// <summary>
    /// Per-side commission payable at position entry. Returns a negative value (cost).
    /// Callers deduct this from the running balance immediately at position open.
    /// </summary>
    public static decimal ComputeEntryCommission(
        decimal lots,
        SymbolInfo symbol,
        decimal entryPrice,
        Func<string, string, decimal> getCrossRate,
        decimal? commissionPerMillion = null)
    {
        var perSide = ComputePerSideCommission(lots, symbol, entryPrice, getCrossRate, commissionPerMillion);
        return -perSide; // costs are negative
    }

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

        var perSide = ComputePerSideCommission(lots, symbol, entryPrice.Value, getCrossRate, commissionPerMillion);
        var commission = -(perSide * 2m); // round-trip, negative

        var nights = CountNightsHeld(openedAtUtc, closedAtUtc, symbol.TripleSwapWeekday,
            dailyResetUtc ?? DefaultDailyResetUtc);
        var swapRate = direction == TradeDirection.Long
            ? swapLongPerLotPerNight ?? symbol.SwapLongPerLotPerNight
            : swapShortPerLotPerNight ?? symbol.SwapShortPerLotPerNight;
        var swap = -(nights * swapRate * lots);

        var net = gross + commission + swap;
        return new TradeCosts(gross, commission, swap, net, nights);
    }

    private static decimal ComputePerSideCommission(
        decimal lots, SymbolInfo symbol, decimal price,
        Func<string, string, decimal> getCrossRate, decimal? commissionPerMillion)
    {
        // F39: a commissionPerMillion override is — by its name, and by what cTrader's --commission
        // flag means — a rate per MILLION USD of notional. It must carry its own formula with it. The
        // old code substituted it for `rate` and then dispatched on the SYMBOL's CommissionType, so a
        // symbol declared AbsolutePerLot (every FX pair in symbols.json) billed the run $30 PER LOT
        // instead of $30 per million: the tape charged $106.80 round-turn on 1.78 EURUSD lots where
        // cTrader, given the identical --commission=30, charged $10.60.
        if (commissionPerMillion is { } perMillion)
        {
            return UsdToAccount(
                NotionalUsd(lots, symbol, price, getCrossRate) * perMillion / 1_000_000m,
                symbol, getCrossRate);
        }

        var rate = symbol.CommissionPerLotPerSide;

        return symbol.CommissionType switch
        {
            // The venue declares a per-lot fee in the ACCOUNT's own currency, so it needs no conversion.
            CommissionType.AbsolutePerLot or CommissionType.None or CommissionType.Unknown
                => lots * rate,

            // These three are all defined against USD notional, so they produce USD and must be
            // converted into the account currency — otherwise a EUR/GBP account books a USD commission
            // against EUR/GBP gross and the two don't add up. On a USD account the conversion is 1.
            CommissionType.UsdPerMillionUsdVolume
                => UsdToAccount(NotionalUsd(lots, symbol, price, getCrossRate) * rate / 1_000_000m,
                    symbol, getCrossRate),

            CommissionType.Pips
                => UsdToAccount(lots * rate * (decimal)symbol.PipSize * BaseToUsd(symbol, price, getCrossRate),
                    symbol, getCrossRate),

            CommissionType.PercentOfNotionalValue
                => UsdToAccount(NotionalUsd(lots, symbol, price, getCrossRate) * rate / 100m,
                    symbol, getCrossRate),

            _ => lots * rate,
        };
    }

    /// <summary>
    /// F34: converts a USD-denominated cost into the account's denomination. Identity on a USD account.
    /// Without it, an EUR account books commission in USD against gross in EUR — the tape over-billed a
    /// EUR account by exactly the EURUSD rate (17% on the first live EUR compare-both).
    /// </summary>
    private static decimal UsdToAccount(
        decimal amountUsd, SymbolInfo symbol, Func<string, string, decimal> getCrossRate)
        => symbol.AccountCurrency == "USD" ? amountUsd : amountUsd * getCrossRate("USD", symbol.AccountCurrency);

    /// <summary>The position's notional value in USD: <c>lots × contractSize × baseToUsdRate</c>.</summary>
    private static decimal NotionalUsd(
        decimal lots, SymbolInfo symbol, decimal price, Func<string, string, decimal> getCrossRate)
        => lots * symbol.ContractSize * BaseToUsd(symbol, price, getCrossRate);

    /// <summary>
    /// Returns the price of 1 unit of the symbol's base currency in USD.
    /// For USD-quoted symbols (EURUSD, XAUUSD): the price IS the USD rate.
    /// For USD-based symbols (USDCAD, USDJPY): 1 USD = 1 USD.
    /// For cross pairs (EURJPY, EURGBP): delegates to the cross-rate function.
    /// </summary>
    private static decimal BaseToUsd(SymbolInfo symbol, decimal price, Func<string, string, decimal> getCrossRate)
    {
        if (symbol.QuoteCurrency == "USD")
            return price;
        if (symbol.BaseCurrency == "USD")
            return 1m;
        return getCrossRate(symbol.BaseCurrency, "USD");
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
