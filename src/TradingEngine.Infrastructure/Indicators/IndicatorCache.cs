using System.Collections.Concurrent;

namespace TradingEngine.Infrastructure.Indicators;

internal sealed class IndicatorCache
{
    private readonly ConcurrentDictionary<string, double> _cache = new();

    public string BuildKey(Symbol symbol, Timeframe tf, string indicatorName, int period, int barCount)
        => $"{symbol}:{tf}:{indicatorName}:{period}:{barCount}";

    public double? Get(string key)
    {
        return _cache.TryGetValue(key, out var value) ? value : null;
    }

    public void Set(string key, double value)
    {
        _cache[key] = value;
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
