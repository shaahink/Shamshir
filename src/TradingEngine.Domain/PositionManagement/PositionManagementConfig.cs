namespace TradingEngine.Domain;

public record PositionManagementConfig(
    string StrategyId,
    TrailingConfig TrailingStop,
    bool UseBreakeven,
    double BreakevenTriggerR,
    Pips BreakevenBufferPips,
    Money InitialRiskAmount)
{
    // iter-38 A4 (PartialTp add-on): close a fraction of the position once at the trigger R-multiple; the
    // remainder keeps trailing. Off by default ⇒ no partial, golden byte-identical.
    public bool PartialTpEnabled { get; init; }
    public double PartialTpTriggerR { get; init; } = 1.0;
    public double PartialTpCloseFraction { get; init; } = 0.5;
}
