namespace TradingEngine.Strategies.TrendBreakout;

public sealed record TrendBreakoutConfig
{
    public string Id { get; init; } = "trend-breakout";
    public string DisplayName { get; init; } = "Trend Breakout v1";
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> Symbols { get; init; } = Array.Empty<string>();
    public string RiskProfileId { get; init; } = "standard";
    public TrendBreakoutParameters Parameters { get; init; } = new();
}

public sealed record TrendBreakoutParameters
{
    public int LookbackBars { get; init; } = 20;
    public int MaPeriod { get; init; } = 50;
    public int AtrPeriod { get; init; } = 14;
    public double SlAtrMultiple { get; init; } = 1.5;
    public double TpRrMultiple { get; init; } = 2.0;
    public string TrailingMethod { get; init; } = "AtrMultiple";
    public double TrailingAtrMultiple { get; init; } = 1.0;
}
