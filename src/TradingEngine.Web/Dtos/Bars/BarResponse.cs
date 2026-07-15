namespace TradingEngine.Web.Dtos.Bars;

public sealed record BarResponse
{
    public long Time { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
}
