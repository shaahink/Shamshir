namespace TradingEngine.Infrastructure.MarketData;

/// <summary>
/// Canonical market-data bar row (iter-marketdata-tape P1). Prices stored as REAL (double) — cTrader's own
/// bar prices are doubles, so this is faithful AND compact for high-volume history (the run DB stores money
/// as TEXT for exactness; market data is high-volume input data, so we deliberately do NOT copy that here —
/// see PLAN §5). Uniqueness is enforced on (Symbol, Timeframe, OpenTimeUtc).
/// </summary>
public sealed class MarketDataBarRow
{
    public long Id { get; set; }
    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "";
    public DateTime OpenTimeUtc { get; set; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public double Volume { get; set; }
    public string Source { get; set; } = "";
    public int Quality { get; set; }
    public DateTime IngestedAtUtc { get; set; }
}
