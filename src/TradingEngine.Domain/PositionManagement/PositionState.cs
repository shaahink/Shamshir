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
    decimal InitialSlDistance = 0,
    string? CloseReason = null,
    string OrderEntryMethod = "Market",
    string? EntryReason = null,
    string? EntryRegime = null,
    // P0.1: the stop-loss price at ORDER CREATION, set once in PositionLifecycle.CreateIntended and
    // never touched by any later `with` (breakeven/trailing move CurrentStopLoss, not this). Carried
    // onto PublishTradeClosed so R-multiple is computed against the risk actually taken at entry.
    Price InitialStopLoss = default);
