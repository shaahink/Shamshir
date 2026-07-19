namespace TradingEngine.Strategies.AsiaRange;

public sealed record AsiaRangeParameters
{
    public int AtrPeriod { get; init; } = 14;

    /// <summary>Tokyo range-build window start (inclusive). Frozen = 00:00 UTC.</summary>
    public TimeOnly RangeStartUtc { get; init; } = new(0, 0);

    /// <summary>Tokyo range-build window end (exclusive). Frozen = 06:00 UTC.</summary>
    public TimeOnly RangeEndUtc { get; init; } = new(6, 0);

    /// <summary>Breakout-entry window start (inclusive). Frozen = 07:00 UTC — note the deliberate
    /// 06:00-07:00 gap between the range build and the entry window (pre-registered window 07:00-10:00).</summary>
    public TimeOnly EntryWindowStartUtc { get; init; } = new(7, 0);

    /// <summary>Breakout-entry window end (exclusive). Frozen = 10:00 UTC.</summary>
    public TimeOnly EntryWindowEndUtc { get; init; } = new(10, 0);

    /// <summary>Session-end daily flatten time.</summary>
    public TimeOnly FlattenTimeUtc { get; init; } = new(16, 0);
}

public sealed record AsiaRangeConfig(
    string Id,
    string DisplayName,
    bool Enabled,
    string RiskProfileId,
    AsiaRangeParameters Parameters) : IStrategyConfig
{
    public RegimeFilterOptions RegimeFilter { get; init; } = new();
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public PositionManagementOptions PositionManagement { get; init; } = new();
    public ReentryOptions Reentry { get; init; } = new();
    public Timeframe EntryTimeframe { get; init; } = Timeframe.M15;
    public string? Symbol { get; init; }
    public IReadOnlyList<Timeframe> RequiredTimeframes { get; init; } = [];

    public TimeOnly? FlattenAtUtc => Parameters.FlattenTimeUtc;
}
