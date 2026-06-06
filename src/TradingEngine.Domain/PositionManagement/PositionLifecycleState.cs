namespace TradingEngine.Domain;

public enum PositionLifecycleState
{
    Active,
    BreakevenSet,
    Trailing,
    PartialClosed,
    Closed,
}
