namespace TradingEngine.Domain;

public record Bar(
    Symbol Symbol,
    Timeframe Timeframe,
    DateTime OpenTimeUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    double Volume,
    decimal? Spread = null)
{
    public bool IsBullish => Close >= Open;
    public decimal Body => Math.Abs(Close - Open);
    public decimal Range => High - Low;
}
