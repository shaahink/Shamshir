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
    EngineMode Mode = EngineMode.Backtest,
    string OrderEntryMethod = "Market",
    Guid OrderId = default,
    string? Timeframe = null,
    string? EntryReason = null,
    string? EntryRegime = null,
    string? EntrySnapshotJson = null,
    string? ExitDetailJson = null,
    // P0.1: the stop-loss price at order creation — never mutated by breakeven/trailing. Nullable
    // because historical trades predating this field may not have it until the backfill runs.
    Price? InitialStopLoss = null,
    // P4.1 (F12): R-normalized adverse/favourable excursions — makes MAE/MFE comparable across asset classes
    // with different pip-convention sizes (EURUSD 0.0001 vs XAUUSD 0.01 vs BTC 1.0). Nullable because
    // historical trades predating this field will have null until the backfill runs.
    double? MaeR = null,
    double? MfeR = null)
{
    public double DurationSeconds => (ClosedAtUtc - OpenedAtUtc).TotalSeconds;
}
