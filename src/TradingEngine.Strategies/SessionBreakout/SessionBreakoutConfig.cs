namespace TradingEngine.Strategies.SessionBreakout;

public sealed record SessionBreakoutParameters
{
    public int AtrPeriod { get; init; } = 14;
    public TimeOnly RangeStartUtc { get; init; } = new(5, 0);
    public TimeOnly RangeEndUtc { get; init; } = new(7, 0);
    public TimeOnly EntryWindowEndUtc { get; init; } = new(9, 0);
    public TimeOnly FlattenTimeUtc { get; init; } = new(12, 0);
}

public sealed record SessionBreakoutConfig(
    string Id,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<string> Symbols,
    string RiskProfileId,
    SessionBreakoutParameters Parameters,
    Timeframe Timeframe = Timeframe.H1) : IStrategyConfig
{
    public RegimeFilterOptions RegimeFilter { get; init; } = new();
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public PositionManagementOptions PositionManagement { get; init; } = new();
    public ReentryOptions Reentry { get; init; } = new();
}
