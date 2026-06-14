namespace TradingEngine.Domain;

public sealed record EngineState(
    IReadOnlyDictionary<Guid, PositionState> Positions,
    GovernorState Governor,
    DrawdownState Drawdown,
    int OpenPositionCount)
{
    public static EngineState Empty => new(
        new Dictionary<Guid, PositionState>(),
        new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
        new DrawdownState(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "Fixed"),
        0);
}
