namespace TradingEngine.Strategies.DayOfWeek;

public sealed record DayOfWeekParameters
{
    public int AtrPeriod { get; init; } = 14;

    /// <summary>The exact bar-open time-of-day at which the entry fires on a listed weekday. Frozen = 00:00 UTC.</summary>
    public TimeOnly EntryHourUtc { get; init; } = new(0, 0);

    /// <summary>End-of-day flatten time. Frozen = 23:00 UTC.</summary>
    public TimeOnly FlattenTimeUtc { get; init; } = new(23, 0);

    /// <summary>Weekday names (case-insensitive <see cref="System.DayOfWeek"/> values) on which to enter.
    /// Frozen default = Monday.</summary>
    public IReadOnlyList<string> Weekdays { get; init; } = ["Monday"];

    /// <summary>Trade direction — <c>"Long"</c> (frozen default) or <c>"Short"</c>.</summary>
    public string Direction { get; init; } = "Long";
}

public sealed record DayOfWeekConfig(
    string Id,
    string DisplayName,
    bool Enabled,
    string RiskProfileId,
    DayOfWeekParameters Parameters) : IStrategyConfig
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
