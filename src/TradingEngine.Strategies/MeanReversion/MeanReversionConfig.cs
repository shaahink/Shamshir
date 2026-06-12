namespace TradingEngine.Strategies.MeanReversion;

public sealed record MeanReversionParameters
{
    public int RsiPeriod { get; init; } = 14;
    public double RsiOversold { get; init; } = 35;
    public double RsiOverbought { get; init; } = 65;
    public int BbPeriod { get; init; } = 20;
    public double BbStdDev { get; init; } = 2.0;
    public int AtrPeriod { get; init; } = 14;
}

public sealed record MeanReversionConfig(
    string Id,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<string> Symbols,
    string RiskProfileId,
    MeanReversionParameters Parameters,
    Timeframe Timeframe = Timeframe.H1) : IStrategyConfig
{
    public RegimeFilterOptions RegimeFilter { get; init; } = new();
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public PositionManagementOptions PositionManagement { get; init; } = new();
    public ReentryOptions Reentry { get; init; } = new();
}
