namespace TradingEngine.Domain;

public sealed record PartialClose(Guid PositionId, decimal CloseLots, string Reason) : PositionModification(PositionId);
