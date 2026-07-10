namespace TradingEngine.Infrastructure.MarketData;

using TradingEngine.Domain;

public sealed record DataQualityReport(
    DateTime GeneratedAtUtc,
    IReadOnlyList<SymbolCoverageEntry> Coverage,
    IReadOnlyList<OhlcViolation> OhlcViolations,
    IReadOnlyList<GapEntry> GapEntries,
    IReadOnlyList<CrossCheckEntry> CrossCheckEntries)
{
    public int TotalBars => (int)Coverage.Sum(c => c.BarCount);
    public int TotalViolations => OhlcViolations.Count + GapEntries.Count(g => !g.StraddlesWeekend);
}

public sealed record SymbolCoverageEntry(
    string Symbol,
    string Timeframe,
    string Source,
    DateTime FirstBarUtc,
    DateTime LastBarUtc,
    long BarCount);

public sealed record OhlcViolation(
    string Symbol,
    string Timeframe,
    DateTime OpenTimeUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close);

public sealed record GapEntry(
    string Symbol,
    string Timeframe,
    DateTime AfterOpenUtc,
    DateTime NextOpenUtc,
    int MissingBars,
    bool StraddlesWeekend);

public sealed record CrossCheckEntry(
    string Symbol,
    string BaseTimeframe,
    string UpperTimeframe,
    int AggregatedBarCount,
    int UpperBarCount,
    int MatchCount,
    int MismatchCount,
    decimal MaxPriceDelta);

public sealed class DataQualityValidator
{
    private readonly IMarketDataStore _marketData;
    private readonly ISymbolInfoRegistry _symbols;

    public DataQualityValidator(IMarketDataStore marketData, ISymbolInfoRegistry symbols)
    {
        _marketData = marketData;
        _symbols = symbols;
    }

    public async Task<DataQualityReport> GenerateReportAsync(CancellationToken ct = default)
    {
        var inventory = await _marketData.GetInventoryAsync(ct);
        var coverage = inventory
            .Select(i => new SymbolCoverageEntry(
                i.Symbol, i.Timeframe.ToString(), i.Source,
                i.FirstOpenUtc, i.LastOpenUtc, i.BarCount))
            .OrderBy(c => c.Symbol).ThenBy(c => c.Timeframe)
            .ToList();

        var ohlcViolations = new List<OhlcViolation>();
        var gapEntries = new List<GapEntry>();
        var crossCheckEntries = new List<CrossCheckEntry>();

        // Group by (symbol, tf) to avoid re-reading the same data
        var groups = inventory.GroupBy(i => (Symbol: Symbol.Parse(i.Symbol), i.Timeframe)).ToList();

        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var symbol = g.Key.Symbol;
                var tf = g.Key.Timeframe;

                var bars = await _marketData.ReadBarsAsync(
                    symbol, tf, g.Min(x => x.FirstOpenUtc), g.Max(x => x.LastOpenUtc), ct);

                if (bars.Count == 0) continue;

                // OHLC sanity
                foreach (var b in bars)
                {
                    var upper = Math.Max(b.Open, b.Close);
                    var lower = Math.Min(b.Open, b.Close);
                    if (b.High < upper || b.Low > lower)
                    {
                        ohlcViolations.Add(new OhlcViolation(
                            symbol.Value, tf.ToString(), b.OpenTimeUtc,
                            b.Open, b.High, b.Low, b.Close));
                    }
                }

                // Gaps — use existing infrastructure
                var from = g.Min(x => x.FirstOpenUtc);
                var to = g.Max(x => x.LastOpenUtc);
                var gaps = await _marketData.GetGapsAsync(symbol, tf, from, to, ct);
                var nonWeekendGaps = gaps.Where(gap => !gap.StraddlesWeekend).ToList();
                gapEntries.AddRange(nonWeekendGaps.Select(g => new GapEntry(
                    symbol.Value, tf.ToString(), g.AfterOpenUtc, g.NextOpenUtc,
                    g.MissingBars, g.StraddlesWeekend)));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* corrupt data — skip cell */ }
        }

        // Cross-TF consistency: M1 aggregates → H1
        var m1Symbols = inventory
            .Where(i => i.Timeframe == Timeframe.M1)
            .Select(i => i.Symbol)
            .Distinct()
            .ToList();
        var h1Symbols = new HashSet<string>(
            inventory.Where(i => i.Timeframe == Timeframe.H1).Select(i => i.Symbol),
            StringComparer.OrdinalIgnoreCase);

        foreach (var symStr in m1Symbols.Where(s => h1Symbols.Contains(s)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var symbol = Symbol.Parse(symStr);
                var h1Inventory = inventory.FirstOrDefault(i =>
                    string.Equals(i.Symbol, symStr, StringComparison.OrdinalIgnoreCase)
                    && i.Timeframe == Timeframe.H1);
                var m1Inventory = inventory.FirstOrDefault(i =>
                    string.Equals(i.Symbol, symStr, StringComparison.OrdinalIgnoreCase)
                    && i.Timeframe == Timeframe.M1);

                if (h1Inventory is null || m1Inventory is null) continue;

                // Read a sample: last 7 days of H1 bars
                var to = h1Inventory.LastOpenUtc;
                var from = to.AddDays(-7);
                if (from < m1Inventory.FirstOpenUtc) from = m1Inventory.FirstOpenUtc;

                var h1Bars = await _marketData.ReadBarsAsync(symbol, Timeframe.H1, from, to, ct);
                var m1Bars = await _marketData.ReadBarsAsync(symbol, Timeframe.M1, from, to, ct);

                if (h1Bars.Count == 0 || m1Bars.Count == 0) continue;

                var matchCount = 0;
                var mismatchCount = 0;
                decimal maxDelta = 0;

                foreach (var h1 in h1Bars)
                {
                    var bucketEnd = h1.OpenTimeUtc + TimeSpan.FromHours(1);
                    var bucket = m1Bars
                        .Where(m => m.OpenTimeUtc >= h1.OpenTimeUtc && m.OpenTimeUtc < bucketEnd)
                        .ToList();

                    if (bucket.Count == 0) continue;

                    var aggOpen = bucket[0].Open;
                    var aggClose = bucket[^1].Close;
                    var aggHigh = bucket.Max(b => b.High);
                    var aggLow = bucket.Min(b => b.Low);

                    var openDelta = Math.Abs(aggOpen - h1.Open);
                    var closeDelta = Math.Abs(aggClose - h1.Close);
                    var highDelta = Math.Abs(aggHigh - h1.High);
                    var lowDelta = Math.Abs(aggLow - h1.Low);
                    var delta = new[] { openDelta, closeDelta, highDelta, lowDelta }.Max();

                    var pipSize = _symbols.TryGet(symbol, out var info) && info.PipSize > 0
                        ? info.PipSize : 0.0001m;

                    if (delta <= pipSize)
                    {
                        matchCount++;
                    }
                    else
                    {
                        mismatchCount++;
                        if (delta > maxDelta) maxDelta = delta;
                    }
                }

                crossCheckEntries.Add(new CrossCheckEntry(
                    symStr, "M1", "H1", m1Bars.Count, h1Bars.Count,
                    matchCount, mismatchCount, maxDelta));
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip */ }
        }

        return new DataQualityReport(
            DateTime.UtcNow, coverage, ohlcViolations, gapEntries, crossCheckEntries);
    }
}
