namespace TradingEngine.Services;

public sealed class PositionManager : IPositionManager
{
    public IReadOnlyList<PositionModification> Evaluate(
        Position position,
        Tick currentTick,
        IReadOnlyList<Bar> recentBars)
    {
        return Array.Empty<PositionModification>();
    }

    public void RegisterPosition(Position position, PositionManagementConfig config) { }
    public void DeregisterPosition(Guid positionId) { }
}
