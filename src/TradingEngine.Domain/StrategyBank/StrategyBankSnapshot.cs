namespace TradingEngine.Domain;

public record StrategyBankSnapshot
{
    public IReadOnlyList<StrategyStatus> Strategies { get; init; } = [];
}

public record StrategyStatus
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public bool IsEnabled { get; init; }
    public required StrategyPerformanceStats Stats { get; init; }
}

public record StrategyPerformanceStats
{
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public decimal TotalPnL { get; init; }
    public decimal ProfitFactor { get; init; }
    public int WinStreak { get; init; }
    public int LossStreak { get; init; }
    public MarketRegime LastRegime { get; init; }
    public double WinRate => TotalTrades > 0 ? (double)WinningTrades / TotalTrades : 0;
}
