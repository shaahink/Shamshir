using System.Collections.Concurrent;

namespace TradingEngine.Infrastructure.Indicators;

public sealed class IndicatorCache
{
    private readonly ConcurrentDictionary<string, double> _cache = new();

    public static string BuildKey(Symbol symbol, Timeframe tf, string indicatorName, int period, int barCount)
        => $"{symbol}:{tf}:{indicatorName}:{period}:{barCount}";

    public static string BuildKey(Symbol symbol, IndicatorRequest req)
    {
        var tf = req.Timeframe == default ? Timeframe.H1 : req.Timeframe;
        return $"{symbol}:{tf}:{req.Type}:{req.Period}:{req.StdDev:F2}:{req.Param1}:{req.Param2:F2}";
    }

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
