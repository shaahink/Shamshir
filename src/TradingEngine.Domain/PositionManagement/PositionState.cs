namespace TradingEngine.Domain;

public sealed record PositionState(
    Guid PositionId,
    Guid OrderId,
    Symbol Symbol,
    TradeDirection Direction,
    decimal Lots,
    Price EntryPrice,
    Price CurrentStopLoss,
    Price? TakeProfit,
    DateTime OpenedAtUtc,
    string StrategyId,
    PositionPhase Phase,
    decimal FilledLots = 0,
    string? RejectionReason = null,
    decimal HighWater = 0,
    decimal LowWater = 0,
    bool BreakevenApplied = false,
    decimal InitialSlDistance = 0);
