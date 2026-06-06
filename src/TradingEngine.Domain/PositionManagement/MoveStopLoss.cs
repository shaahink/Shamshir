namespace TradingEngine.Domain;

public sealed record MoveStopLoss(Guid PositionId, Price NewStopLoss) : PositionModification(PositionId);
