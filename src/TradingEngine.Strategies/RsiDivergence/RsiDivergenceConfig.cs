namespace TradingEngine.Strategies.RsiDivergence;

public sealed record RsiDivergenceConfig : IStrategyConfig
{
    public string Id { get; init; } = "rsi-divergence";
    public string DisplayName { get; init; } = "RSI Divergence";
    public bool Enabled { get; init; } = true;
    public string RiskProfileId { get; init; } = "standard";
    public RegimeFilterOptions RegimeFilter { get; init; } = new() { AllowTrending = false, AllowHighVolatility = false };
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public PositionManagementOptions PositionManagement { get; init; } = new();
    public ReentryOptions Reentry { get; init; } = new();
    public RsiDivergenceParameters Parameters { get; init; } = new();
    public Timeframe EntryTimeframe { get; init; } = Timeframe.H1;
    public string? Symbol { get; init; }
    public IReadOnlyList<Timeframe> RequiredTimeframes { get; init; } = [];
}

public sealed record RsiDivergenceParameters
{
    public int RsiPeriod { get; init; } = 14;
    public int DivergenceLookback { get; init; } = 10;
    public int AtrPeriod { get; init; } = 14;
}
