namespace TradingEngine.Domain;

public record TradeIntent(
    Symbol Symbol,
    TradeDirection Direction,
    OrderType OrderType,
    Price? LimitPrice,
    Price StopLoss,
    Price? TakeProfit,
    string StrategyId,
    string RiskProfileId,
    string Reason,
    DateTime CreatedAtUtc);
