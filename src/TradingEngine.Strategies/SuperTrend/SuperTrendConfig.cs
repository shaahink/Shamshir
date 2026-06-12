namespace TradingEngine.Strategies.SuperTrend;

public sealed record SuperTrendConfig : IStrategyConfig
{
    public string Id { get; init; } = "super-trend";
    public string DisplayName { get; init; } = "SuperTrend";
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> Symbols { get; init; } = ["EURUSD", "GBPUSD"];
    public string RiskProfileId { get; init; } = "standard";
    public Timeframe Timeframe { get; init; } = Timeframe.H1;
    public RegimeFilterOptions RegimeFilter { get; init; } = new() { AllowRanging = false };
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public PositionManagementOptions PositionManagement { get; init; } = new();
    public ReentryOptions Reentry { get; init; } = new();
    public SuperTrendParameters Parameters { get; init; } = new();
}

public sealed record SuperTrendParameters
{
    public int AtrPeriod { get; init; } = 10;
    public double AtrMultiplier { get; init; } = 3.0;
    public int AdxPeriod { get; init; } = 14;
    public double AdxMinThreshold { get; init; } = 20.0;
}
