namespace TradingEngine.Domain;

public record TpParameters(
    Pips? Pips,
    double? RRRatio,
    double? AtrMultiplier);
