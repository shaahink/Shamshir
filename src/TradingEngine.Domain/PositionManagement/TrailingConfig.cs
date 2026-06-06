namespace TradingEngine.Domain;

public record TrailingConfig(
    TrailingMethod Method,
    double StepPips,
    double AtrMultiple,
    double BreakevenTriggerR);
