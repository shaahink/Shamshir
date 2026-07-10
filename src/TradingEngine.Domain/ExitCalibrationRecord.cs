namespace TradingEngine.Domain;

/// <summary>P3.4 — the exit-rule values calibrated for one (strategy, symbol, timeframe, regime) cell.
/// Used by <see cref="AddOnResolver"/> when <see cref="AddOnMode.Calibrated"/> is set.</summary>
public sealed record ExitCalibrationRecord
{
    public double SlAtrMultiple { get; init; } = 4.0;
    public double? TpRrMultiple { get; init; }
    public double? BeTriggerR { get; init; }
    public double? BeOffsetPips { get; init; }
    public double? TrailAtrMultiple { get; init; }
    public double? PartialTriggerR { get; init; }
    public double? PartialCloseFraction { get; init; }
}

/// <summary>P3.4 — lookup for calibrated exit rules. Resolves one cell by (strategyId, symbol, timeframe, regime).</summary>
public interface IExitCalibrationLookup
{
    ExitCalibrationRecord? Get(string strategyId, string symbol, Timeframe timeframe, string? regime);
}
