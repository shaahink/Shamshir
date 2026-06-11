namespace TradingEngine.Risk.Filters;

public sealed class ConfigurableNewsFilter : INewsFilter
{
    private readonly IReadOnlyList<NewsBlockWindow> _windows;

    public ConfigurableNewsFilter(IReadOnlyList<NewsBlockWindow> windows)
    {
        _windows = windows;
    }

    public bool IsNewsWindowActive(Symbol symbol, DateTime utcNow)
    {
        var symStr = symbol.Value;
        foreach (var w in _windows)
        {
            if (w.Symbol != "*" && w.Symbol != symStr) continue;
            if (w.DayOfWeek.HasValue && w.DayOfWeek.Value != utcNow.DayOfWeek) continue;
            if (utcNow.TimeOfDay >= w.StartUtc && utcNow.TimeOfDay <= w.EndUtc) return true;
        }
        return false;
    }
}
