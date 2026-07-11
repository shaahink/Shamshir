namespace TradingEngine.Domain;

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
    CommissionType CommissionType = CommissionType.AbsolutePerLot);
