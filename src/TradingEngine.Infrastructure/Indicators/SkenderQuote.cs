using Skender.Stock.Indicators;

namespace TradingEngine.Infrastructure.Indicators;

public sealed class SkenderQuote(Bar bar) : IQuote
{
    public DateTime Date => bar.OpenTimeUtc;
    public decimal Open => bar.Open;
    public decimal High => bar.High;
    public decimal Low => bar.Low;
    public decimal Close => bar.Close;
    public decimal Volume => (decimal)bar.Volume;
}
