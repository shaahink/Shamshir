namespace TradingEngine.Strategies.EmaAlignment;

public sealed record EmaAlignmentParameters
{
    public int FastPeriod { get; init; } = 20;
    public int SlowPeriod { get; init; } = 50;
    public int AtrPeriod { get; init; } = 14;

    /// <summary>P2.3/D5: how many bars back to search for the EMA crossover that defines the current
    /// trend leg — the edge is "crossover, then first pullback touch of the fast EMA", not the raw
    /// fast&gt;slow CONDITION (which fires every bar of any trend).</summary>
    public int CrossoverLookback { get; init; } = 20;
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
