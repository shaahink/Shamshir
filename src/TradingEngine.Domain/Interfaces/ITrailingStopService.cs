namespace TradingEngine.Domain;

public interface ITrailingStopService
{
    Price? Evaluate(
        Position position,
        Tick currentTick,
        TrailingConfig config,
        IReadOnlyList<Bar> recentBars);
}
