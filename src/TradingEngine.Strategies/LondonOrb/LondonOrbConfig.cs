namespace TradingEngine.Strategies.LondonOrb;

public sealed record LondonOrbParameters
{
    public int AtrPeriod { get; init; } = 14;

    /// <summary>Opening-range build window (start-inclusive).</summary>
    public TimeOnly RangeStartUtc { get; init; } = new(7, 0);

    /// <summary>Opening-range build window end (exclusive).</summary>
    public TimeOnly RangeEndUtc { get; init; } = new(8, 0);

    /// <summary>Breakout-entry window start (start-inclusive). Frozen = London open, 08:00.</summary>
    public TimeOnly EntryWindowStartUtc { get; init; } = new(8, 0);

    /// <summary>Breakout-entry window end (exclusive).</summary>
    public TimeOnly EntryWindowEndUtc { get; init; } = new(11, 0);

    /// <summary>Session-end daily flatten time (loop-level via <see cref="LondonOrbConfig.FlattenAtUtc"/>).</summary>
    public TimeOnly FlattenTimeUtc { get; init; } = new(16, 0);
}

public sealed record LondonOrbConfig(
    string Id,
    string DisplayName,
    bool Enabled,
    string RiskProfileId,
    LondonOrbParameters Parameters) : IStrategyConfig
{
    public RegimeFilterOptions RegimeFilter { get; init; } = new();
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public PositionManagementOptions PositionManagement { get; init; } = new();
    public ReentryOptions Reentry { get; init; } = new();
    public Timeframe EntryTimeframe { get; init; } = Timeframe.M15;
    public string? Symbol { get; init; }
    public IReadOnlyList<Timeframe> RequiredTimeframes { get; init; } = [];

    // Wires the session-end daily flatten via KernelTimeFlattenEvaluator (same seam as session-breakout).
    public TimeOnly? FlattenAtUtc => Parameters.FlattenTimeUtc;
}
