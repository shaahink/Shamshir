namespace TradingEngine.Domain;

public abstract record EngineEffect;

public record SubmitOrder(Guid OrderId, Symbol Symbol, TradeDirection Direction, decimal Lots, Price? LimitPrice, Price StopLoss, Price? TakeProfit, string StrategyId) : EngineEffect;

public record ModifyStopLoss(Guid PositionId, Price NewStopLoss) : EngineEffect;

public record ModifyTakeProfit(Guid PositionId, Price NewTakeProfit) : EngineEffect;

public record CloseOpenPosition(Guid PositionId, string Reason) : EngineEffect;

public record RecordDecisionEvent(DecisionRecord Decision) : EngineEffect;
