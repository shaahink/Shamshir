namespace TradingEngine.Services.TrailingStop;

public sealed class TrailingStopService : ITrailingStopService
{
    public Price? Evaluate(
        Position position,
        Tick currentTick,
        TrailingConfig config,
        IReadOnlyList<Bar> recentBars)
    {
        throw new NotImplementedException(
            "TrailingStopService requires SymbolInfo and IIndicatorService. " +
            "Use TrailingHelpers directly with the appropriate context.");
    }
}
