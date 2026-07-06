namespace TradingEngine.Domain.Interfaces;

/// <summary>
/// Reads pre-computed reference volatilities (rolling-median ATR, median bar range) from the
/// ReferenceScales table. Used by UnitConversion and AddOnAutoTuner to replace the spread-guess
/// heuristic with measured values. Single purpose, sync, expected to be fast (one indexed row).
/// </summary>
public interface IReferenceScaleLookup
{
    double? GetMedianAtrPips(Symbol symbol, Timeframe tf);
    double? GetMedianBarRangePips(Symbol symbol, Timeframe tf);
    int? GetSampleBarCount(Symbol symbol, Timeframe tf);
}
