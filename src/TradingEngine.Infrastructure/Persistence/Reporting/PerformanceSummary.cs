namespace TradingEngine.Infrastructure.Persistence.Reporting;

public sealed record PerformanceSummary(
    int TotalTrades,
    int Wins,
    decimal TotalNetPnL,
    decimal MaxSingleLoss,
    double WorstMAE,
    double AvgHoldHours);
