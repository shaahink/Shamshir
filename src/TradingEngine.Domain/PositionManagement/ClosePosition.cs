namespace TradingEngine.Domain;

public sealed record ClosePosition(Guid PositionId, string Reason) : PositionModification(PositionId);
