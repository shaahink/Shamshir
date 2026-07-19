namespace TradingEngine.Domain;

/// <param name="SwapLongPerLotPerNight">Overnight financing for a LONG, in <b>PIPS per lot per night</b>,
///     signed as a P&amp;L adjustment: <b>negative = the trader pays</b>, positive = the trader receives.
///     These are the venue's own units and sign (cTrader declares <c>SwapCalculationType=Pips</c>) — see
///     <c>TradeCostCalculator</c>, which converts to money via the pip value. P4.4/F45: this was
///     previously consumed as if it were MONEY per lot per night, and then negated, so the tape paid the
///     trader to hold a position the broker was charging for.</param>
/// <param name="SwapShortPerLotPerNight">As above, for a SHORT.</param>
public record SymbolInfo(
    Symbol Symbol,
    SymbolCategory Category,
    string BaseCurrency,
    string QuoteCurrency,
    decimal PipSize,
    decimal TickSize,
    decimal ContractSize,
    decimal MinLots,
    decimal MaxLots,
    decimal LotStep,
    decimal MarginRate,
    decimal TypicalSpread,
    string AccountCurrency = "USD",
    decimal CommissionPerLotPerSide = 0,
    decimal SwapLongPerLotPerNight = 0,
    decimal SwapShortPerLotPerNight = 0,
    string TripleSwapWeekday = "Wednesday",
    CommissionType CommissionType = CommissionType.AbsolutePerLot,
    string SwapCalculationType = "Pips");
