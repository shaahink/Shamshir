namespace TradingEngine.Strategies.MacdMomentum;

public sealed record MacdMomentumConfig : IStrategyConfig
{
    public string Id { get; init; } = "macd-momentum";
    public string DisplayName { get; init; } = "MACD Momentum";
    public bool Enabled { get; init; } = true;
    public string RiskProfileId { get; init; } = "standard";
    public RegimeFilterOptions RegimeFilter { get; init; } = new() { AllowRanging = false };
    public OrderEntryOptions OrderEntry { get; init; } = new();
    public PositionManagementOptions PositionManagement { get; init; } = new();
    public ReentryOptions Reentry { get; init; } = new();
    public MacdMomentumParameters Parameters { get; init; } = new();
}

public sealed record MacdMomentumParameters
{
    public int MacdFast { get; init; } = 12;
    public int MacdSlow { get; init; } = 26;
    public int MacdSignal { get; init; } = 9;
    public int SmaPeriod { get; init; } = 200;
    public int AdxPeriod { get; init; } = 14;
    public double AdxMinThreshold { get; init; } = 20.0;
    public int AtrPeriod { get; init; } = 14;
    public double SlAtrMultiple { get; init; } = 2.0;
    public double TpRrMultiple { get; init; } = 3.0;
}
