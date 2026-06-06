namespace TradingEngine.Services.SLTPCalculation;

public sealed class SlTpCalculator : ISlTpCalculator
{
    public Price CalculateStopLoss(
        Price entryPrice,
        TradeDirection direction,
        SlMethod method,
        SlParameters parameters,
        IReadOnlyList<Bar> recentBars)
    {
        if (recentBars.Count == 0)
            throw new ArgumentException("recentBars must not be empty", nameof(recentBars));

        var symbol = recentBars[0].Symbol;
        var symbolInfo = new SymbolInfo(symbol, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

        return method switch
        {
            SlMethod.FixedPips => SlTpHelpers.FixedPip(entryPrice, direction,
                parameters.Pips ?? new Pips(20), symbolInfo),
            SlMethod.AtrMultiple => SlTpHelpers.AtrBased(entryPrice, direction,
                parameters.AtrMultiplier ?? 1.5, parameters.AtrMultiplier ?? 1.5, symbolInfo),
            SlMethod.SwingBased => SlTpHelpers.SwingBased(entryPrice, direction,
                recentBars, parameters.LookbackBars ?? 10,
                parameters.BufferPips ?? new Pips(1), symbolInfo),
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        };
    }

    public Price? CalculateTakeProfit(
        Price entryPrice,
        Price stopLoss,
        TradeDirection direction,
        TpMethod method,
        TpParameters parameters)
    {
        var symbolInfo = new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

        if (method == TpMethod.None) return null;

        return method switch
        {
            TpMethod.RRMultiple => SlTpHelpers.RRMultiple(entryPrice, stopLoss,
                direction, parameters.RRRatio ?? 2.0, symbolInfo),
            TpMethod.AtrMultiple => SlTpHelpers.AtrMultiple(entryPrice, direction,
                parameters.AtrMultiplier ?? 1.5, parameters.AtrMultiplier ?? 1.5, symbolInfo),
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        };
    }
}
