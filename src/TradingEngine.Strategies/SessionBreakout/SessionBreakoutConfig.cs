namespace TradingEngine.Strategies.SessionBreakout;

public sealed record SessionBreakoutParameters
{
    public int AtrPeriod { get; init; } = 14;
    public double SlAtrMultiple { get; init; } = 1.5;
    public double TpRrMultiple { get; init; } = 2.0;
    public TimeOnly RangeStartUtc { get; init; } = new(5, 0);
    public TimeOnly RangeEndUtc { get; init; } = new(7, 0);
    public TimeOnly EntryWindowEndUtc { get; init; } = new(9, 0);
    public TimeOnly FlattenTimeUtc { get; init; } = new(12, 0);
}

public sealed record SessionBreakoutConfig(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Symbols,
    string RiskProfileId,
    SessionBreakoutParameters Parameters) : IStrategyConfig;
