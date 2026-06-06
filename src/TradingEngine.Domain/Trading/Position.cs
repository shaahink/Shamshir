namespace TradingEngine.Domain;

public record Position(
    Guid Id,
    Guid OrderId,
    Symbol Symbol,
    TradeDirection Direction,
    decimal Lots,
    Price EntryPrice,
    Price CurrentStopLoss,
    Price? TakeProfit,
    DateTime OpenedAtUtc,
    string StrategyId);
