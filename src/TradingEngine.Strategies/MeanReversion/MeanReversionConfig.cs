namespace TradingEngine.Strategies.MeanReversion;

public sealed record MeanReversionParameters
{
    public int RsiPeriod { get; init; } = 14;
    public int BbPeriod { get; init; } = 20;
    public double BbStdDev { get; init; } = 2.0;
    public int AtrPeriod { get; init; } = 14;
    public double SlAtrMultiple { get; init; } = 1.5;
    public double TpRrMultiple { get; init; } = 1.0;
}

public sealed record MeanReversionConfig(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Symbols,
    string RiskProfileId,
    MeanReversionParameters Parameters,
    Timeframe Timeframe = Timeframe.H1) : IStrategyConfig;
