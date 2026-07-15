namespace TradingEngine.Domain;

public sealed record MoveStopLoss(Guid PositionId, Price NewStopLoss, string Reason = "TRAIL") : PositionModification(PositionId);
