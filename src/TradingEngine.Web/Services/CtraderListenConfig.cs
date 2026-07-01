namespace TradingEngine.Web.Services;

public sealed record CtraderListenConfig
{
    public string[]? StrategyIds { get; init; }
    public string? RiskProfileId { get; init; }
    public decimal? InitialBalance { get; init; }
    public bool GovernorEnabled { get; init; } = true;
    public bool DailyDdEnabled { get; init; } = true;
    public bool MaxDdEnabled { get; init; } = true;
    public bool StripAddOns { get; init; }
    public bool RegimeDisabled { get; init; }
    public string? PackId { get; init; }
    public double CommissionPerMillion { get; init; } = 50;
    public double SpreadPips { get; init; } = 1;
}

public enum ListenState
{
    Idle,
    Listening,
    Active
}
