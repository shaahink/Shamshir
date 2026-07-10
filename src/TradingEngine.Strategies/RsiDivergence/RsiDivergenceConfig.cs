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

    /// <summary>P2.2: the total span searched for a divergence pivot pair (decline → bounce → second
    /// decline easily spans dozens of bars for a real double-bottom/top — this is not just margin around
    /// a single point).</summary>
    public int DivergenceLookback { get; init; } = 50;
    public int AtrPeriod { get; init; } = 14;

    /// <summary>P2.2: fractal pivot confirmation strength — a swing point needs this many bars with a
    /// strictly worse extreme on EACH side to confirm. 2 is a standard, fast-confirming default.</summary>
    public int PivotStrength { get; init; } = 2;
}
