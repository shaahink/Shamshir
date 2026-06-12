namespace TradingEngine.Domain.Events;

public record PositionPartiallyClosed(
    Guid PositionId,
    decimal ClosedLots,
    decimal RemainingLots,
    decimal FillPrice,
    DateTime AtUtc) : EngineEvent(AtUtc);
