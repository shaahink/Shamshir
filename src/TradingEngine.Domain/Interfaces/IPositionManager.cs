namespace TradingEngine.Domain;

public interface IPositionManager
{
    IReadOnlyList<PositionModification> Evaluate(
        Position position,
        Tick currentTick,
        IReadOnlyList<Bar> recentBars);

    void RegisterPosition(Position position, PositionManagementConfig config);
    void DeregisterPosition(Guid positionId);
}
