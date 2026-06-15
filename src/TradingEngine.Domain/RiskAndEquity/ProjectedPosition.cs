namespace TradingEngine.Domain;

public sealed record ProjectedPosition(
    decimal SlPips,
    decimal Lots,
    decimal PipValuePerLot);
