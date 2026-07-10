namespace TradingEngine.Domain;

public record RiskProfile(
    string Id,
    string DisplayName,
    double RiskPerTradePercent,
    double MaxDailyDrawdownPercent,
    double MaxTotalDrawdownPercent,
    double MaxSlPips,
    double MaxExposurePercent,
    double DrawdownScaleThreshold,
    double DrawdownScaleFloor,
    int MaxConcurrentPositions,
    bool AllowHedging,
    string PropFirmRuleSetId,
    LotSizingMethod LotSizingMethod = LotSizingMethod.PercentRisk,
    decimal FixedLots = 0.1m,
    decimal FixedDollarRisk = 0m,
    double KellyFraction = 0.25,
    double AntiMartingaleMultiplier = 1.5,
    int AntiMartingaleMaxSteps = 3)
{
    public double MaxExposurePerCurrencyPercent { get; init; } = 0.05;
    public SizeModifierOptions SizeModifiers { get; init; } = new();

    /// <summary>iter-quant-model P2.6 (D9, units doctrine): normalized replacement for <see cref="MaxSlPips"/>
    /// — a flat pip ceiling silently rejects/crushes every gold/crypto trade whose natural stop distance in
    /// pips dwarfs a forex-calibrated cap. When set, <c>UnitConversion.ResolveMaxSlPips</c> overrides
    /// <see cref="MaxSlPips"/> with <c>MaxSlAtrMultiple × referenceAtrPips(symbol, timeframe)</c> per proposal.</summary>
    public double? MaxSlAtrMultiple { get; init; }
}

