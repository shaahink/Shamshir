namespace TradingEngine.Services.SLTPCalculation;

public sealed class SlTpCalculator(ISymbolInfoRegistry symbolRegistry) : ISlTpCalculator
{
    public Price CalculateStopLoss(
        Price entryPrice,
        TradeDirection direction,
        SlMethod method,
        SlParameters parameters,
        IReadOnlyList<Bar> recentBars)
    {
        var symbol = recentBars.Count > 0 ? recentBars[0].Symbol : Symbol.Parse("EURUSD");
        var symbolInfo = symbolRegistry.Get(symbol);

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
        var symbolInfo = symbolRegistry.Get(Symbol.Parse("EURUSD"));

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
