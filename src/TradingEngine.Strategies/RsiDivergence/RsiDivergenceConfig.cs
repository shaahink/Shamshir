namespace TradingEngine.Strategies.RsiDivergence;

public sealed record RsiDivergenceConfig : IStrategyConfig
{
    public string Id { get; init; } = "rsi-divergence";
    public string DisplayName { get; init; } = "RSI Divergence";
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> Symbols { get; init; } = ["EURUSD", "GBPUSD", "USDJPY"];
    public string RiskProfileId { get; init; } = "standard";
    public Timeframe Timeframe { get; init; } = Timeframe.H1;
    public RegimeFilterOptions RegimeFilter { get; init; } = new() { AllowTrending = false, AllowHighVolatility = false };
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public RsiDivergenceParameters Parameters { get; init; } = new();
}

public sealed record RsiDivergenceParameters
{
    public int RsiPeriod { get; init; } = 14;
    public int DivergenceLookback { get; init; } = 10;
    public int AtrPeriod { get; init; } = 14;
    public double SlAtrMultiple { get; init; } = 1.5;
    public double TpRrMultiple { get; init; } = 2.0;
}
