namespace TradingEngine.Domain;

public sealed record EngineState(
    IReadOnlyDictionary<Guid, PositionState> Positions,
    GovernorTradingState GovernorState,
    string GovernorReason,
    decimal GovernorSizeMultiplier,
    DrawdownState Drawdown,
    int OpenPositionCount)
{
    public static EngineState Empty => new(
        new Dictionary<Guid, PositionState>(),
        GovernorTradingState.Normal,
        "Initial",
        1.0m,
        new DrawdownState(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "Fixed"),
        0);
}
