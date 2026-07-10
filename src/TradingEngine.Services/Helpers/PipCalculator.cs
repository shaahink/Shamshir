namespace TradingEngine.Services.Helpers;

public static class PipCalculator
{
    public static Pips Distance(Price from, Price to, SymbolInfo symbol)
    {
        var rawDistance = Math.Abs(to.Value - from.Value);
        return new Pips((double)(rawDistance / symbol.PipSize));
    }

    public static decimal PipValuePerLot(
        SymbolInfo symbol,
        decimal currentPrice,
        Func<string, string, decimal> getCrossRate)
    {
        var rawPipValue = symbol.PipSize * symbol.ContractSize;

        if (symbol.QuoteCurrency == symbol.AccountCurrency)
            return rawPipValue;

        if (symbol.BaseCurrency == symbol.AccountCurrency)
        {
            if (currentPrice == 0)
                throw new InvalidOperationException("Price cannot be zero");
            return rawPipValue / currentPrice;
        }

        var conversionRate = getCrossRate(symbol.QuoteCurrency, symbol.AccountCurrency);
        return rawPipValue * conversionRate;
    }

    public static Money GrossPnL(
        TradeDirection direction,
        Price entryPrice,
        Price exitPrice,
        decimal lots,
        SymbolInfo symbol,
        Func<string, string, decimal> getCrossRate)
    {
        var priceDiff = direction == TradeDirection.Long
            ? exitPrice.Value - entryPrice.Value
            : entryPrice.Value - exitPrice.Value;

        var pipsMoved = priceDiff / symbol.PipSize;
        var pipValue = PipValuePerLot(symbol, exitPrice.Value, getCrossRate);
        var grossAmount = pipsMoved * pipValue * lots;

        return new Money(grossAmount, symbol.AccountCurrency);
    }

    public static decimal FloatingPnL(
        Position position,
        Tick currentTick,
        SymbolInfo symbol,
        Func<string, string, decimal> getCrossRate)
    {
        var closingPrice = position.Direction == TradeDirection.Long
            ? currentTick.Bid
            : currentTick.Ask;

        var priceDiff = position.Direction == TradeDirection.Long
            ? closingPrice - position.EntryPrice.Value
            : position.EntryPrice.Value - closingPrice;

        var pipValue = PipValuePerLot(symbol, closingPrice, getCrossRate);
        return (priceDiff / symbol.PipSize) * pipValue * position.Lots;
    }

    public static double RMultiple(Money netPnL, Money initialRiskAmount)
    {
        if (initialRiskAmount.Amount == 0) return 0;
        return (double)(netPnL.Amount / initialRiskAmount.Amount);
    }

    // P0.1: R as a price-distance ratio (reward over risk taken AT ENTRY). initialStopLoss must be the
    // stop recorded when the order was created (PositionState.InitialStopLoss) — NEVER the current/final
    // stop, which breakeven/trailing may have moved to near-zero risk by the time the trade closes.
    public static double RMultiple(TradeDirection direction, Price entryPrice, Price exitPrice, Price initialStopLoss)
    {
        var signedMove = direction == TradeDirection.Long
            ? exitPrice.Value - entryPrice.Value
            : entryPrice.Value - exitPrice.Value;
        var riskDistance = Math.Abs(entryPrice.Value - initialStopLoss.Value);
        return riskDistance > 0 ? (double)(signedMove / riskDistance) : 0d;
    }
}
