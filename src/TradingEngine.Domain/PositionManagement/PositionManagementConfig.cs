namespace TradingEngine.Domain;

public record PositionManagementConfig(
    string StrategyId,
    TrailingConfig TrailingStop,
    bool UseBreakeven,
    double BreakevenTriggerR,
    Pips BreakevenBufferPips,
    Money InitialRiskAmount);
