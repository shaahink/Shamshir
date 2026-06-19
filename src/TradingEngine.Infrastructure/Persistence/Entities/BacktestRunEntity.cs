namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class BacktestRunEntity
{
    public string RunId { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public string Symbol { get; set; } = "";
    public string Period { get; set; } = "";
    public string Symbols { get; set; } = "[]";
    public string Periods { get; set; } = "[]";
    public DateTime BacktestFrom { get; set; }
    public DateTime BacktestTo { get; set; }
    public decimal InitialBalance { get; set; }
    public string AlgoHash { get; set; } = "";
    public string StrategyParamsJson { get; set; } = "{}";
    public string? EffectiveConfigJson { get; set; }
    public decimal NetProfit { get; set; }
    public decimal GrossPnL { get; set; }
    public decimal CommissionTotal { get; set; }
    public decimal SwapTotal { get; set; }
    public decimal MaxDrawdownPct { get; set; }
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public double WinRatePct { get; set; }
    public int ExitCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ReportJsonPath { get; set; }
}
