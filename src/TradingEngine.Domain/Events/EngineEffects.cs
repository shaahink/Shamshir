namespace TradingEngine.Domain;

public abstract record EngineEffect;

public record SubmitOrder(Guid OrderId, Symbol Symbol, TradeDirection Direction, decimal Lots, Price? LimitPrice, Price StopLoss, Price? TakeProfit, string StrategyId) : EngineEffect;

public record ModifyStopLoss(Guid PositionId, Price NewStopLoss) : EngineEffect;

public record ModifyTakeProfit(Guid PositionId, Price NewTakeProfit) : EngineEffect;

// D1 (iter-26): the Guid here is the VENUE order id, not the engine-internal PositionId.
// Venues (simulated/replay/cTrader) own open positions by the order/client id returned from
// SubmitOrderAsync, so every venue-bound close must carry OrderId. Passing the internal
// PositionId here made RequestForceCloseAll/CloseRequested a silent no-op against the venue.
public record CloseOpenPosition(Guid OrderId, string Reason) : EngineEffect;

public record RecordDecisionEvent(DecisionRecord Decision) : EngineEffect;

// GrossProfit/NetProfit/Commission/Swap carry the venue-authoritative PnL when known (live);
// they stay null for the simulated venue, where EffectExecutor recomputes gross from prices.
// HighWater/LowWater are the most-favorable/most-adverse prices reached over the position's
// life (from PositionState's per-bar tracking); EffectExecutor derives MAE/MFE from them.
public record PublishTradeClosed(Guid PositionId, Symbol Symbol, TradeDirection Direction, decimal Lots, Price EntryPrice, Price ExitPrice, Price StopLoss, Price? TakeProfit, string StrategyId, string ExitReason, DateTime ClosedAtUtc, DateTime OpenedAtUtc, string? RiskProfileId = null, decimal? GrossProfit = null, decimal? NetProfit = null, decimal? Commission = null, decimal? Swap = null, decimal HighWater = 0, decimal LowWater = 0) : EngineEffect;

public record RegisterRisk(Guid PositionId, string StrategyId, decimal RiskAmount) : EngineEffect;

public record DeregisterRisk(Guid PositionId) : EngineEffect;
