namespace TradingEngine.Domain;

public record SlParameters(
    Pips? Pips,
    double? AtrMultiplier,
    int? LookbackBars,
    Pips? BufferPips);
