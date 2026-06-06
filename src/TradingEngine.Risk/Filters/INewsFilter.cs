namespace TradingEngine.Risk.Filters;

public interface INewsFilter
{
    bool IsNewsWindowActive(Symbol symbol, DateTime utcNow);
}
