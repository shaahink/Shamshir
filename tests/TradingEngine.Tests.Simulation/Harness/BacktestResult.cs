namespace TradingEngine.Tests.Simulation.Harness;

public sealed record BacktestResult(
    decimal NetPnL,
    decimal MaxDrawdown,
    int TotalTrades,
    double WinRate,
    IReadOnlyList<TradeResult> Trades,
    IReadOnlyList<RiskViolation> PropFirmViolations);
