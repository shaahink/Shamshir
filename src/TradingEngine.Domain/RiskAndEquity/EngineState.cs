namespace TradingEngine.Domain;

/// <summary>
/// Kernel state. Only the Positions slice is wired via PositionTracker + EngineReducer.
/// Governor and Drawdown are frozen at Empty and never reflect runtime reality;
/// RiskManager owns the authoritative drawdown and governor state imperatively.
/// See SYSTEM-MODEL.md §3.2.
/// </summary>
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
