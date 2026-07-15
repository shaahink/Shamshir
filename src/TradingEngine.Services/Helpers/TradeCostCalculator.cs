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
/// <c>Net = Gross + Commission + Swap</c>. Commission is always a cost (negative). Swap is NOT negated:
/// the broker's rate is already signed as a P&amp;L adjustment, so a negative rate is already a cost and
/// a positive one is genuinely a credit (P4.4/F45).</para>
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
/// <para>Swap model (P4.4/F45, measured against cTrader): the rates on <see cref="SymbolInfo"/> are
/// <b>PIPS per lot per night</b> (the venue declares <c>SwapCalculationType=Pips</c>), signed as a P&amp;L
/// adjustment. Money = <c>nights × ratePips × lots × pipValueInAccountCurrency</c>. Saturday and Sunday
/// rollovers are NOT charged — the market is shut, which is why Wednesday is billed triple.</para>
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

        // P4.4 (F46): each side is billed against ITS OWN notional, so the closing side is priced at the
        // EXIT price — not the entry price twice. Commission scales with notional (lots × contract × price),
        // and the venue charges the close when the close happens.
        //
        // On EURUSD this is invisible: price moves ~0.5% over a trade, so entry ≈ exit notional and the
        // error hides inside the 2% budget (it measured 0.53%). On XAUUSD, where price moves hundreds of
        // dollars, the same bug measured 10.2% and failed the gate. One test symbol hides scale-dependent
        // bugs — INVESTIGATION-METHOD.
        var entrySideCommission = ComputePerSideCommission(lots, symbol, entryPrice.Value, getCrossRate, commissionPerMillion);
        var exitSideCommission = ComputePerSideCommission(lots, symbol, exitPrice.Value, getCrossRate, commissionPerMillion);
        var commission = -(entrySideCommission + exitSideCommission); // round-trip, negative

        var nights = CountNightsHeld(openedAtUtc, closedAtUtc, symbol.TripleSwapWeekday,
            dailyResetUtc ?? DefaultDailyResetUtc);
        var swapRatePips = direction == TradeDirection.Long
            ? swapLongPerLotPerNight ?? symbol.SwapLongPerLotPerNight
            : swapShortPerLotPerNight ?? symbol.SwapShortPerLotPerNight;

        // P4.4 (F45): swap rates are PIPS per lot per night, already SIGNED as a P&L adjustment —
        // negative = the trader pays. The venue declares both facts itself (`SwapCalculationType=Pips`,
        // `swapLong=-2.445` on a EURUSD long that cTrader then charged for). Two bugs lived here:
        //
        //   * the rate was treated as MONEY per lot per night, so it was never multiplied by the pip
        //     value — an 8.6x understatement on EURUSD (the same units bug as F39, in swap this time);
        //   * it was NEGATED, which turns the venue's signed cost into a CREDIT. The tape paid the
        //     trader 2.55 to hold a long that the broker charged 35.90 for.
        //
        // Multiply by the pip value in ACCOUNT currency (PipValuePerLot converts) and do NOT negate.
        var pipValue = PipCalculator.PipValuePerLot(symbol, exitPrice.Value, getCrossRate);
        var swap = nights * swapRatePips * lots * pipValue;

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
    /// Counts the swap-charging rollover boundaries (default 22:00 UTC) strictly crossed between open and
    /// close. The triple-swap weekday (Wednesday) counts 3×; a trade that crosses no rollover holds zero
    /// nights.
    ///
    /// <para>P4.4 (F45): SATURDAY AND SUNDAY ROLLOVERS ARE NOT CHARGED. The market is shut, so no broker
    /// finances a position over them — that is the whole reason Wednesday is billed triple. Counting them
    /// made a Friday-to-Monday hold cost 3 nights instead of 1, which is exactly what the tape did.</para>
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
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            count += day.DayOfWeek == triple ? 3 : 1;
        }
        return count;
    }
}
