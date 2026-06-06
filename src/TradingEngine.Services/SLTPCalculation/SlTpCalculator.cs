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
        throw new NotImplementedException("Requires SymbolInfo and indicator values — use SlTpHelpers directly in strategy context.");
    }

    public Price? CalculateTakeProfit(
        Price entryPrice,
        Price stopLoss,
        TradeDirection direction,
        TpMethod method,
        TpParameters parameters)
    {
        throw new NotImplementedException("Requires SymbolInfo — use SlTpHelpers directly in strategy context.");
    }
}
