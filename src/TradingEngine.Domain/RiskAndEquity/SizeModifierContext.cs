namespace TradingEngine.Domain;

public record SizeModifierContext
{
    public required EquitySnapshot Equity { get; init; }
    public required RiskProfile Profile { get; init; }
    public required TradeIntent Intent { get; init; }
    public double? CurrentAtr { get; init; }
    public IReadOnlyList<double> AtrBaseline { get; init; } = [];
    public TimeSpan UtcTimeOfDay { get; init; }
    public int StrategyWinStreak { get; init; }
    public int StrategyLossStreak { get; init; }
}
