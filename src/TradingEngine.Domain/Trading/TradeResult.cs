namespace TradingEngine.Domain;

public record TradeResult(
    Guid Id,
    Guid PositionId,
    Symbol Symbol,
    TradeDirection Direction,
    decimal Lots,
    Price EntryPrice,
    Price ExitPrice,
    Price StopLoss,
    Price? TakeProfit,
    DateTime OpenedAtUtc,
    DateTime ClosedAtUtc,
    Money GrossPnL,
    Money Commission,
    Money Swap,
    Money NetPnL,
    Pips PnLPips,
    double RMultiple,
    Pips MaxAdverseExcursion,
    Pips MaxFavorableExcursion,
    string ExitReason,
    string StrategyId,
    string RiskProfileId,
    EngineMode Mode = EngineMode.Backtest)
{
    public double DurationSeconds => (ClosedAtUtc - OpenedAtUtc).TotalSeconds;
}
