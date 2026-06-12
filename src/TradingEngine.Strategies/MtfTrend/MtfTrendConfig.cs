namespace TradingEngine.Strategies.MtfTrend;

public sealed record MtfTrendConfig : IStrategyConfig
{
    public string Id { get; init; } = "mtf-trend";
    public string DisplayName { get; init; } = "Multi-Timeframe Trend";
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> Symbols { get; init; } = ["EURUSD", "GBPUSD"];
    public string RiskProfileId { get; init; } = "standard";
    public Timeframe Timeframe { get; init; } = Timeframe.H1;
    public RegimeFilterOptions RegimeFilter { get; init; } = new() { AllowRanging = false };
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public PositionManagementOptions PositionManagement { get; init; } = new();
    public ReentryOptions Reentry { get; init; } = new();
    public MtfTrendParameters Parameters { get; init; } = new();
    public Timeframe HigherTimeframe { get; init; } = Timeframe.H4;
}

public sealed record MtfTrendParameters
{
    public int EmaPeriod { get; init; } = 200;
    public int RsiPeriod { get; init; } = 14;
    public double RsiBullishPullback { get; init; } = 45.0;
    public double RsiBearishPullback { get; init; } = 55.0;
    public int SwingLookback { get; init; } = 10;
    public int AtrPeriod { get; init; } = 14;
    public double SlAtrMinMultiple { get; init; } = 1.5;
    public double TpRrMultiple { get; init; } = 2.0;
}
