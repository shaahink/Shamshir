namespace TradingEngine.Domain;

public sealed record ProjectedPosition(
    string Symbol,
    decimal SlPips,
    decimal Lots,
    decimal PipValuePerLot);
