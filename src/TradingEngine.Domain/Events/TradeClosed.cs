namespace TradingEngine.Domain;

// ExcursionPathJson (P3.1): the recorded MAE/MFE path, if the venue recorded one -- kept separate from
// TradeResult (a new TradeExcursions table, not a new TradeResult column) so TradePersistenceHandler can
// write-through to both without changing TradeResult's shape.
public sealed record TradeClosed(TradeResult Result, string RunId, DateTime OccurredAtUtc, string? ExcursionPathJson = null) : EngineEvent(OccurredAtUtc);
