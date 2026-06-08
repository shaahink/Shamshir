namespace TradingEngine.CTraderRunner;

public sealed record BacktestResult
{
    public required string RunId { get; init; }
    public int ExitCode { get; init; }
    public bool Success => ExitCode == 0;
    public decimal NetProfit { get; init; }
    public decimal MaxDrawdownPct { get; init; }
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public double WinRatePct { get; init; }
    public string AlgoHash { get; init; } = "";
    public string? ErrorMessage { get; init; }
}
