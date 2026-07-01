namespace TradingEngine.Domain;

public abstract record EngineEffect;

public record SubmitOrder(Guid OrderId, Symbol Symbol, TradeDirection Direction, decimal Lots, Price? LimitPrice, Price StopLoss, Price? TakeProfit, string StrategyId) : EngineEffect;

// TakeProfit (iter-36 K4 gap-3): carries the position's current TP so a trailing modify preserves it on
// the venue (the simulated venue clears TP if null is passed). The reducer sets it from the position.
public record ModifyStopLoss(Guid PositionId, Price NewStopLoss, Price? TakeProfit = null) : EngineEffect;

public record ModifyTakeProfit(Guid PositionId, Price NewTakeProfit) : EngineEffect;

// D1 (iter-26): the Guid here is the VENUE order id, not the engine-internal PositionId.
// Venues (simulated/replay/cTrader) own open positions by the order/client id returned from
// SubmitOrderAsync, so every venue-bound close must carry OrderId. Passing the internal
// PositionId here made RequestForceCloseAll/CloseRequested a silent no-op against the venue.
// ExitPrice (iter-36 K2): the price an engine-detected SL/TP exit should fill at (the stop/target price),
// so a backtest close fills there instead of the bar close (F2/D3). The reducer sets it from the position's
// SL/TP; the EffectExecutor routes it to ClosePositionAtAsync. Null = market close (force-close / live, where
// the venue fills server-side).
public record CloseOpenPosition(Guid OrderId, string Reason, Price? ExitPrice = null) : EngineEffect;

// iter-38 A4 (PartialTp): close PART of an open position. OrderId is the venue order id (like
// CloseOpenPosition); the venue's partial fill reduces the position and the remainder stays open.
public record ClosePartialOpenPosition(Guid OrderId, decimal CloseLots, string Reason) : EngineEffect;

public record RecordDecisionEvent(DecisionRecord Decision) : EngineEffect;

// GrossProfit/NetProfit/Commission/Swap carry the venue-authoritative PnL when known (live);
// they stay null for the simulated venue, where EffectExecutor recomputes gross from prices.
// HighWater/LowWater are the most-favorable/most-adverse prices reached over the position's
// life (from PositionState's per-bar tracking); EffectExecutor derives MAE/MFE from them.
// OrderId is the venue-facing clientOrderId (the position's originating order), carried through to the
// persisted trade so the venue ledger (cBot report.json) joins to DB trades exactly. PositionId remains
// the engine-internal position identity.
public record PublishTradeClosed(Guid PositionId, Symbol Symbol, TradeDirection Direction, decimal Lots, Price EntryPrice, Price ExitPrice, Price StopLoss, Price? TakeProfit, string StrategyId, string ExitReason, DateTime ClosedAtUtc, DateTime OpenedAtUtc, Guid OrderId = default, string? RiskProfileId = null, string OrderEntryMethod = "Market", decimal? GrossProfit = null, decimal? NetProfit = null, decimal? Commission = null, decimal? Swap = null, decimal HighWater = 0, decimal LowWater = 0) : EngineEffect;

public record RegisterRisk(Guid PositionId, string StrategyId, decimal RiskAmount) : EngineEffect;

public record DeregisterRisk(Guid PositionId) : EngineEffect;
