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

    // P2.1: a capped ring buffer per sig key (latest last), so strategies that need a short lookback
    // (macd histogram cross, RSI pullback cross, SuperTrend direction flip, BB-width history) can read it
    // from MarketContext.IndicatorSeries instead of keeping their own private cadence-fragile field, which
    // silently desyncs if a bar is ever skipped/replayed out of order.
    private const int SeriesCapacity = 64;
    private readonly ConcurrentDictionary<string, Queue<double>> _series = new();

    // P1.5.2: aux-timeframe bars (e.g. H4 for mtf-trend) are known for the WHOLE run up front (loaded once
    // by the orchestrator), but must only become visible to indicator computation point-in-time — as of the
    // sim-time of the decision bar currently being evaluated — or a strategy's higher-TF indicator silently
    // sees the future for the rest of the run (the P1.3 lookahead-bias bug this cursor fixes). The full list
    // is held here; AdvanceAuxBarsAsync reveals bars one at a time, gated by close time.
    private sealed class AuxBarCursor
    {
        public required IReadOnlyList<Bar> All { get; init; }
        public int NextIndex;
    }

    private readonly ConcurrentDictionary<(Symbol Symbol, Timeframe Timeframe), AuxBarCursor> _auxSources = new();

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
        _auxSources.Clear();
        _series.Clear();
    }

    // Single write point (per the P2.1 spec): every indicator value assignment goes through here, so the
    // ring buffer can never drift out of sync with IndicatorValues.
    private void Emit(string key, double value)
    {
        IndicatorValues[key] = value;
        var q = _series.GetOrAdd(key, _ => new Queue<double>());
        lock (q)
        {
            q.Enqueue(value);
            while (q.Count > SeriesCapacity) q.Dequeue();
        }
    }

    /// <summary>Snapshot of the last <see cref="SeriesCapacity"/> values for a sig key, oldest first
    /// (latest last). Empty if the key has never been computed.</summary>
    public IReadOnlyList<double> GetSeries(string key)
    {
        if (!_series.TryGetValue(key, out var q)) return [];
        lock (q) { return q.ToArray(); }
    }

    /// <summary>
    /// Register the full known range of an auxiliary timeframe's bars for a symbol (e.g. mtf-trend's H4).
    /// Does NOT make them visible yet — <see cref="AdvanceAuxBarsAsync"/> reveals them incrementally as the
    /// decision-bar loop advances, so no indicator computed from this TF ever sees a bar that hasn't
    /// "happened yet" in sim-time.
    /// </summary>
    public void SetAuxBarSource(Symbol symbol, Timeframe tf, IReadOnlyList<Bar> allBars)
    {
        _auxSources[(symbol, tf)] = new AuxBarCursor { All = allBars };
    }

    /// <summary>
    /// Reveal any aux-TF bars whose close time has arrived as of <paramref name="decisionBarCloseUtc"/>
    /// (the current decision bar's own close), then recompute indicators for any aux TF that advanced —
    /// so a strategy's higher-TF indicator (e.g. mtf-trend's H4 EMA) only ever reflects bars closed as of
    /// "now" in the replay, never the full run's future range.
    /// </summary>
    public async Task AdvanceAuxBarsAsync(Symbol symbol, DateTime decisionBarCloseUtc, CancellationToken ct)
    {
        foreach (var ((sym, tf), cursor) in _auxSources)
        {
            if (sym != symbol) continue;

            var advanced = false;
            while (cursor.NextIndex < cursor.All.Count)
            {
                var auxBar = cursor.All[cursor.NextIndex];
                var auxCloseUtc = auxBar.OpenTimeUtc + tf.ToTimeSpan();
                if (auxCloseUtc > decisionBarCloseUtc) break;

                var byTf = Bars.GetOrAdd(symbol, _ => new());
                var list = byTf.GetOrAdd(tf, _ => new());
                lock (list)
                {
                    list.Add(auxBar);
                    while (list.Count > 500) list.RemoveAt(0);
                }
                cursor.NextIndex++;
                advanced = true;
            }

            if (advanced) await RecomputeIndicatorsAsync(symbol, tf, ct);
        }
    }

    public Task RecomputeIndicatorsAsync(Symbol symbol, Timeframe tf, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Bars.TryGetValue(symbol, out var byTf)) return Task.CompletedTask;
        if (!byTf.TryGetValue(tf, out var list)) return Task.CompletedTask;

        IReadOnlyList<Bar> bars;
        lock (list) { bars = list.ToList(); }

        // F2: convert bars→SkenderQuote once per bar, share across all indicator calls below.
        var quotes = SkenderIndicatorService.ToQuotes(bars);
        var ind = (_indicators as SkenderIndicatorService)!;

        // De-dupe across strategies: many strategies request the same indicator (e.g. ATR_14) on the
        // same symbol/timeframe. Compute each unique signature once per bar.
        var computed = new HashSet<string>();

        foreach (var strategy in _strategies)
        {
            foreach (var req in strategy.RequiredIndicators)
            {
                var reqQuotes = quotes;
                if (req.Timeframe != tf && req.Timeframe != default)
                {
                    if (byTf.TryGetValue(req.Timeframe, out var reqList))
                    {
                        lock (reqList) { reqQuotes = SkenderIndicatorService.ToQuotes(reqList.ToList()); }
                    }
                    else
                    {
                        continue;
                    }
                }

                var sigKey = IndicatorCache.BuildKey(symbol, req);
                if (!computed.Add(sigKey)) continue;
                switch (req.Type)
                {
                    case IndicatorType.Atr:
                        Emit(sigKey, ind.Atr(reqQuotes, req.Period));
                        break;
                    case IndicatorType.Ema:
                        Emit(sigKey, ind.Ema(reqQuotes, req.Period));
                        break;
                    case IndicatorType.Rsi:
                        Emit(sigKey, ind.Rsi(reqQuotes, req.Period));
                        break;
                    case IndicatorType.Sma:
                        Emit(sigKey, ind.Sma(reqQuotes, req.Period));
                        break;
                    case IndicatorType.Adx:
                        Emit(sigKey, ind.Adx(reqQuotes, req.Period));
                        break;
                    case IndicatorType.BollingerBands:
                        var (upper, middle, lower) = ind.BollingerBands(reqQuotes, req.Period, req.StdDev);
                        Emit(sigKey, middle);
                        Emit($"{sigKey}_Upper", upper);
                        Emit($"{sigKey}_Lower", lower);
                        break;
                    case IndicatorType.Macd:
                        var macdFast = req.Period;
                        var macdSlow = req.Param1 > 0 ? req.Param1 : 26;
                        var macdSig = (int)(req.Param2 > 0 ? req.Param2 : 9);
                        var macd = ind.Macd(reqQuotes, macdFast, macdSlow, macdSig);
                        Emit(sigKey, macd.MacdLine);
                        Emit($"{sigKey}_Signal", macd.Signal);
                        Emit($"{sigKey}_Histogram", macd.Histogram);
                        break;
                    case IndicatorType.SuperTrend:
                        var stMult = req.Param2 > 0 ? req.Param2 : 3.0;
                        var st = ind.SuperTrend(reqQuotes, req.Period, stMult);
                        Emit(sigKey, st.Line);
                        Emit($"{sigKey}_Direction", st.Direction);
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

    /// <summary>P2.1: same shape as <see cref="BuildStrategyIndicatorValues"/> but the ring-buffer series
    /// (oldest first, latest last) per key instead of the single latest value.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<double>> BuildStrategyIndicatorSeries(Symbol symbol, IStrategy strategy)
    {
        var dict = new Dictionary<string, IReadOnlyList<double>>();
        foreach (var req in strategy.RequiredIndicators)
        {
            var sigKey = IndicatorCache.BuildKey(symbol, req);
            void AddIfPresent(string fullKey, string outputKey)
            {
                var series = GetSeries(fullKey);
                if (series.Count > 0) dict[outputKey] = series;
            }
            AddIfPresent(sigKey, req.Key);
            AddIfPresent($"{sigKey}_Upper", $"{req.Key}_Upper");
            AddIfPresent($"{sigKey}_Lower", $"{req.Key}_Lower");
            AddIfPresent($"{sigKey}_Signal", $"{req.Key}_Signal");
            AddIfPresent($"{sigKey}_Histogram", $"{req.Key}_Histogram");
            AddIfPresent($"{sigKey}_Direction", $"{req.Key}_Direction");
        }
        return dict;
    }
}
