namespace TradingEngine.Risk.Filters;

public sealed class NewsFilter : INewsFilter
{
    public bool IsNewsWindowActive(Symbol symbol, DateTime utcNow) => false;
}
