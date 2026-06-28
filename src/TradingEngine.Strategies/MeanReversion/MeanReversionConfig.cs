namespace TradingEngine.Strategies.MeanReversion;

public sealed record MeanReversionParameters
{
    public int RsiPeriod { get; init; } = 14;
    public double RsiOversold { get; init; } = 30;
    public double RsiOverbought { get; init; } = 70;
    public int BbPeriod { get; init; } = 20;
    public double BbStdDev { get; init; } = 2.0;
    public int AtrPeriod { get; init; } = 14;

    /// <summary>
    /// How close the bar's close must sit to its extreme (as a fraction of the bar's high-low range)
    /// for an entry: 0.33 ⇒ close must be in the lower (long) / upper (short) third of the bar — a
    /// rejection wick. Replaces a fixed 0.2%-of-price test that was ~20+ pips on H1 majors and thus
    /// almost always true (i.e. no filter at all).
    /// </summary>
    public double ProximityToExtremeFraction { get; init; } = 0.33;
}

public sealed record MeanReversionConfig(
    string Id,
    string DisplayName,
    bool Enabled,
    string RiskProfileId,
    MeanReversionParameters Parameters) : IStrategyConfig
{
    public RegimeFilterOptions RegimeFilter { get; init; } = new();
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public PositionManagementOptions PositionManagement { get; init; } = new();
    public ReentryOptions Reentry { get; init; } = new();
}
