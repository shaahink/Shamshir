namespace TradingEngine.Services.TrailingStop;

public sealed class TrailingStopService : ITrailingStopService
{
    public Price? Evaluate(
        Position position,
        Tick currentTick,
        TrailingConfig config,
        IReadOnlyList<Bar> recentBars)
    {
        var symbolInfo = new SymbolInfo(position.Symbol, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

        return config.Method switch
        {
            TrailingMethod.StepPips => TrailingHelpers.StepTrail(
                position, currentTick.Bid, currentTick.Ask,
                new Pips(config.StepPips), symbolInfo),
            TrailingMethod.AtrMultiple => TrailingHelpers.AtrTrail(
                position, currentTick.Bid > position.EntryPrice.Value ? currentTick.Bid : position.EntryPrice.Value,
                currentTick.Ask < position.EntryPrice.Value ? currentTick.Ask : position.EntryPrice.Value,
                config.AtrMultiple, config.AtrMultiple, symbolInfo),
            TrailingMethod.BreakevenThenTrail => TrailingHelpers.Breakeven(
                position, currentTick.Bid, currentTick.Ask,
                config.BreakevenTriggerR, new Pips(1), symbolInfo),
            _ => null,
        };
    }
}
