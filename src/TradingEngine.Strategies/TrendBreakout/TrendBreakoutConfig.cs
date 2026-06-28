namespace TradingEngine.Strategies.TrendBreakout;

public sealed record TrendBreakoutConfig : IStrategyConfig
{
    public string Id { get; init; } = "trend-breakout";
    public string DisplayName { get; init; } = "Trend Breakout v1";
    public bool Enabled { get; init; } = true;
    public string RiskProfileId { get; init; } = "standard";
    public RegimeFilterOptions RegimeFilter { get; init; } = new();
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public PositionManagementOptions PositionManagement { get; init; } = new();
    public ReentryOptions Reentry { get; init; } = new();
    public TrendBreakoutParameters Parameters { get; init; } = new();
}

public sealed record TrendBreakoutParameters
{
    public int LookbackBars { get; init; } = 20;
    public int MaPeriod { get; init; } = 50;
    public int AtrPeriod { get; init; } = 14;
}
