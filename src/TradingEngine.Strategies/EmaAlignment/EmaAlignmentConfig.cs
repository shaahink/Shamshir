namespace TradingEngine.Strategies.EmaAlignment;

public sealed record EmaAlignmentParameters
{
    public int FastPeriod { get; init; } = 20;
    public int SlowPeriod { get; init; } = 50;
    public int AtrPeriod { get; init; } = 14;
}

public sealed record EmaAlignmentConfig(
    string Id,
    string DisplayName,
    bool Enabled,
    string RiskProfileId,
    EmaAlignmentParameters Parameters) : IStrategyConfig
{
    public RegimeFilterOptions RegimeFilter { get; init; } = new();
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public PositionManagementOptions PositionManagement { get; init; } = new();
    public ReentryOptions Reentry { get; init; } = new();
    public Timeframe EntryTimeframe { get; init; } = Timeframe.H1;
    public string? Symbol { get; init; }
    public IReadOnlyList<Timeframe> RequiredTimeframes { get; init; } = [];
}
