namespace TradingEngine.Strategies.NyOpenDrive;

public sealed record NyOpenDriveParameters
{
    public int AtrPeriod { get; init; } = 14;

    /// <summary>Momentum-continuation signal window start (inclusive). Frozen = NY open, 13:30 UTC.</summary>
    public TimeOnly SignalStartUtc { get; init; } = new(13, 30);

    /// <summary>Signal window end (exclusive).</summary>
    public TimeOnly SignalEndUtc { get; init; } = new(15, 0);

    /// <summary>Session-end daily flatten time.</summary>
    public TimeOnly FlattenTimeUtc { get; init; } = new(20, 0);

    /// <summary><c>"drive"</c> (default, the pre-registered census run) goes WITH the opening drive;
    /// <c>"fade"</c> inverts it. Shipped as a knob but the census runs <c>drive</c>.</summary>
    public string Mode { get; init; } = "drive";
}

public sealed record NyOpenDriveConfig(
    string Id,
    string DisplayName,
    bool Enabled,
    string RiskProfileId,
    NyOpenDriveParameters Parameters) : IStrategyConfig
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
