namespace TradingEngine.Domain;

public record TrailingConfig(
    TrailingMethod Method,
    double StepPips,
    double AtrMultiple,
    double BreakevenTriggerR)
{
    public int StructureLookbackBars { get; init; } = 10;
    public double[] SteppedRLevels { get; init; } = [1.0, 2.0, 3.0];
    public bool RideEnabled { get; init; }
    public double RideAdxFloor { get; init; } = 25;
    public double RideRelaxedAtrMultiple { get; init; } = 3.0;
}
