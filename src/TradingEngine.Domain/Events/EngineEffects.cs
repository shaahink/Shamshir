namespace TradingEngine.Domain;

public abstract record EngineEffect;

public record SubmitOrder(Guid OrderId, Symbol Symbol, TradeDirection Direction, decimal Lots, Price? LimitPrice, Price StopLoss, Price? TakeProfit, string StrategyId) : EngineEffect;

public record ModifyStopLoss(Guid PositionId, Price NewStopLoss) : EngineEffect;

public record ModifyTakeProfit(Guid PositionId, Price NewTakeProfit) : EngineEffect;

public record CloseOpenPosition(Guid PositionId, string Reason) : EngineEffect;

public record RecordDecisionEvent(DecisionRecord Decision) : EngineEffect;

// GrossProfit/NetProfit/Commission/Swap carry the venue-authoritative PnL when known (live);
// they stay null for the simulated venue, where EffectExecutor recomputes gross from prices.
public record PublishTradeClosed(Guid PositionId, Symbol Symbol, TradeDirection Direction, decimal Lots, Price EntryPrice, Price ExitPrice, Price StopLoss, Price? TakeProfit, string StrategyId, string ExitReason, DateTime ClosedAtUtc, DateTime OpenedAtUtc, string? RiskProfileId = null, decimal? GrossProfit = null, decimal? NetProfit = null, decimal? Commission = null, decimal? Swap = null) : EngineEffect;

public record RegisterRisk(Guid PositionId, string StrategyId, decimal RiskAmount) : EngineEffect;

public record DeregisterRisk(Guid PositionId) : EngineEffect;
