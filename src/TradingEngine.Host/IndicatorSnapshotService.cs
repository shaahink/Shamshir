using System.Collections.Concurrent;
using TradingEngine.Infrastructure.Indicators;

namespace TradingEngine.Host;

public sealed class IndicatorSnapshotService
{
    private readonly IIndicatorService _indicators;
    private readonly IReadOnlyList<IStrategy> _strategies;

    public ConcurrentDictionary<Symbol, ConcurrentDictionary<Timeframe, List<Bar>>> Bars { get; } = new();
    public ConcurrentDictionary<string, double> IndicatorValues { get; } = new();
    public Dictionary<string, double> ReusableIndicatorDict { get; } = new();

    public IndicatorSnapshotService(
        IIndicatorService indicators,
        IReadOnlyList<IStrategy> strategies)
    {
        _indicators = indicators;
        _strategies = strategies;
    }

    public void Reset()
    {
        Bars.Clear();
        IndicatorValues.Clear();
        ReusableIndicatorDict.Clear();
    }

    public Task RecomputeIndicatorsAsync(Symbol symbol, Timeframe tf, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Bars.TryGetValue(symbol, out var byTf)) return Task.CompletedTask;
        if (!byTf.TryGetValue(tf, out var list)) return Task.CompletedTask;

        IReadOnlyList<Bar> bars;
        lock (list) { bars = list.ToList(); }

        // De-dupe across strategies: many strategies request the same indicator (e.g. ATR_14) on the
        // same symbol/timeframe. Compute each unique signature once per bar — recomputing per strategy
        // is the (now-removed) reason the indicator service carried a fragile internal cache.
        var computed = new HashSet<string>();

        foreach (var strategy in _strategies)
        {
            foreach (var req in strategy.RequiredIndicators)
            {
                var reqBars = bars;
                if (req.Timeframe != tf && req.Timeframe != default)
                {
                    if (byTf.TryGetValue(req.Timeframe, out var reqList))
                        lock (reqList) { reqBars = reqList.ToList(); }
                    else
                        continue;
                }

                var sigKey = IndicatorCache.BuildKey(symbol, req);
                if (!computed.Add(sigKey)) continue;
                switch (req.Type)
                {
                    case IndicatorType.Atr:
                        IndicatorValues[sigKey] = _indicators.Atr(reqBars, req.Period);
                        break;
                    case IndicatorType.Ema:
                        IndicatorValues[sigKey] = _indicators.Ema(reqBars, req.Period);
                        break;
                    case IndicatorType.Rsi:
                        IndicatorValues[sigKey] = _indicators.Rsi(reqBars, req.Period);
                        break;
                    case IndicatorType.Sma:
                        IndicatorValues[sigKey] = _indicators.Sma(reqBars, req.Period);
                        break;
                    case IndicatorType.Adx:
                        IndicatorValues[sigKey] = _indicators.Adx(reqBars, req.Period);
                        break;
                    case IndicatorType.BollingerBands:
                        var (upper, middle, lower) = _indicators.BollingerBands(reqBars, req.Period, req.StdDev);
                        IndicatorValues[sigKey] = middle;
                        IndicatorValues[$"{sigKey}_Upper"] = upper;
                        IndicatorValues[$"{sigKey}_Lower"] = lower;
                        break;
                    case IndicatorType.Macd:
                        var macdFast = req.Period;
                        var macdSlow = req.Param1 > 0 ? req.Param1 : 26;
                        var macdSig = (int)(req.Param2 > 0 ? req.Param2 : 9);
                        var macd = _indicators.Macd(reqBars, macdFast, macdSlow, macdSig);
                        IndicatorValues[sigKey] = macd.MacdLine;
                        IndicatorValues[$"{sigKey}_Signal"] = macd.Signal;
                        IndicatorValues[$"{sigKey}_Histogram"] = macd.Histogram;
                        break;
                    case IndicatorType.SuperTrend:
                        var stMult = req.Param2 > 0 ? req.Param2 : 3.0;
                        var st = _indicators.SuperTrend(reqBars, req.Period, stMult);
                        IndicatorValues[sigKey] = st.Line;
                        IndicatorValues[$"{sigKey}_Direction"] = st.Direction;
                        break;
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task WarmUpIndicatorsAsync(CancellationToken ct)
    {
        foreach (var (symbol, byTf) in Bars)
        {
            foreach (var (tf, _) in byTf)
            {
                RecomputeIndicatorsAsync(symbol, tf, ct);
            }
        }
        return Task.CompletedTask;
    }

    public IReadOnlyDictionary<Timeframe, IReadOnlyList<Bar>>? BuildBarSnapshot(Symbol symbol)
    {
        if (!Bars.TryGetValue(symbol, out var byTf)) return null;
        var snapshot = new Dictionary<Timeframe, IReadOnlyList<Bar>>();
        foreach (var (tf, list) in byTf)
        {
            lock (list) { snapshot[tf] = list.ToList(); }
        }
        return snapshot;
    }

    public void BuildSharedIndicatorSnapshot(Symbol symbol)
    {
        ReusableIndicatorDict.Clear();
        foreach (var strategy in _strategies)
        {
            foreach (var req in strategy.RequiredIndicators)
            {
                var sigKey = IndicatorCache.BuildKey(symbol, req);
                if (IndicatorValues.TryGetValue(sigKey, out var val))
                    ReusableIndicatorDict[req.Key] = val;
                if (IndicatorValues.TryGetValue($"{sigKey}_Upper", out var upper))
                    ReusableIndicatorDict[$"{req.Key}_Upper"] = upper;
                if (IndicatorValues.TryGetValue($"{sigKey}_Lower", out var lower))
                    ReusableIndicatorDict[$"{req.Key}_Lower"] = lower;
                if (IndicatorValues.TryGetValue($"{sigKey}_Signal", out var signal))
                    ReusableIndicatorDict[$"{req.Key}_Signal"] = signal;
                if (IndicatorValues.TryGetValue($"{sigKey}_Histogram", out var hist))
                    ReusableIndicatorDict[$"{req.Key}_Histogram"] = hist;
                if (IndicatorValues.TryGetValue($"{sigKey}_Direction", out var dir))
                    ReusableIndicatorDict[$"{req.Key}_Direction"] = dir;
            }
        }
    }

    public IReadOnlyDictionary<string, double> BuildStrategyIndicatorValues(Symbol symbol, IStrategy strategy)
    {
        var dict = new Dictionary<string, double>();
        foreach (var req in strategy.RequiredIndicators)
        {
            var sigKey = IndicatorCache.BuildKey(symbol, req);
            if (IndicatorValues.TryGetValue(sigKey, out var val))
                dict[req.Key] = val;
            if (IndicatorValues.TryGetValue($"{sigKey}_Upper", out var upper))
                dict[$"{req.Key}_Upper"] = upper;
            if (IndicatorValues.TryGetValue($"{sigKey}_Lower", out var lower))
                dict[$"{req.Key}_Lower"] = lower;
            if (IndicatorValues.TryGetValue($"{sigKey}_Signal", out var signal))
                dict[$"{req.Key}_Signal"] = signal;
            if (IndicatorValues.TryGetValue($"{sigKey}_Histogram", out var hist))
                dict[$"{req.Key}_Histogram"] = hist;
            if (IndicatorValues.TryGetValue($"{sigKey}_Direction", out var dir))
                dict[$"{req.Key}_Direction"] = dir;
        }
        return dict;
    }
}
