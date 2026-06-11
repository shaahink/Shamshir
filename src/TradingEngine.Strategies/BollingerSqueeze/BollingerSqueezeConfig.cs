namespace TradingEngine.Strategies.BollingerSqueeze;

public sealed record BollingerSqueezeConfig : IStrategyConfig
{
    public string Id { get; init; } = "bb-squeeze";
    public string DisplayName { get; init; } = "Bollinger Squeeze";
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> Symbols { get; init; } = ["EURUSD", "GBPUSD"];
    public string RiskProfileId { get; init; } = "standard";
    public Timeframe Timeframe { get; init; } = Timeframe.H1;
    public RegimeFilterOptions RegimeFilter { get; init; } = new() { AllowHighVolatility = false };
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public PositionManagementOptions PositionManagement { get; init; } = new();
    public BollingerSqueezeParameters Parameters { get; init; } = new();
}

public sealed record BollingerSqueezeParameters
{
    public int BbPeriod { get; init; } = 20;
    public double BbStdDev { get; init; } = 2.0;
    public int AtrPeriod { get; init; } = 14;
    public double SqueezeThreshold { get; init; } = 0.8;
    public int CooldownBars { get; init; } = 3;
    public double SlBandBuffer { get; init; } = 0.5;
    public double TpRrMultiple { get; init; } = 2.5;
}
