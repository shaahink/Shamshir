using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.MarketData.Sync;

/// <summary>One symbol × timeframe cell's coverage + sync state, aggregated across sources.</summary>
public sealed record CoverageRow(
    string Symbol,
    string Timeframe,
    long BarCount,
    DateTime? FirstBarUtc,
    DateTime? LastBarUtc,
    string Status,                 // up-to-date | stale | missing | disabled
    bool InWatchlist,
    bool Enabled,
    DateTime BackfillFromUtc,
    DateTime? SyncFromUtc,         // when a fill is needed, the range to fetch [from,to]
    DateTime? SyncToUtc);

/// <summary>
/// X4 — the single place that computes market-data coverage and the "what needs filling" decision, so
/// the coverage API and the auto-sync loop agree by construction. Aggregates inventory across sources per
/// (symbol, timeframe), overlays the watchlist, and derives a market-hours-aware status + missing tail.
/// </summary>
public sealed class MarketDataCoverageService(IMarketDataStore store, MarketDataSyncStore syncStore)
{
    /// <summary>Allowed lag, in bar-intervals, before a cell is "stale" (absorbs the in-progress bar + slack).</summary>
    private const int StaleToleranceBars = 2;

    public async Task<IReadOnlyList<CoverageRow>> GetCoverageAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var inventory = await store.GetInventoryAsync(ct);
        var watch = await syncStore.ListAsync(ct);

        var rows = new Dictionary<(string Sym, string Tf), Accum>();

        foreach (var i in inventory)
        {
            var key = (i.Symbol.ToUpperInvariant(), i.Timeframe.ToString().ToLowerInvariant());
            if (!rows.TryGetValue(key, out var acc))
            {
                acc = new Accum();
                rows[key] = acc;
            }
            acc.BarCount += i.BarCount;
            acc.First = acc.First is null || i.FirstOpenUtc < acc.First ? i.FirstOpenUtc : acc.First;
            acc.Last = acc.Last is null || i.LastOpenUtc > acc.Last ? i.LastOpenUtc : acc.Last;
        }

        foreach (var w in watch)
        {
            var key = (w.Symbol.ToUpperInvariant(), w.Timeframe.ToLowerInvariant());
            if (!rows.TryGetValue(key, out var acc))
            {
                acc = new Accum();
                rows[key] = acc;
            }
            acc.InWatchlist = true;
            acc.Enabled = w.Enabled;
            acc.BackfillFrom = DateTime.SpecifyKind(w.BackfillFromUtc, DateTimeKind.Utc);
        }

        var result = new List<CoverageRow>();
        foreach (var ((sym, tf), acc) in rows)
        {
            var tfEnum = Enum.TryParse<Timeframe>(tf, ignoreCase: true, out var parsed) ? parsed : Timeframe.H1;
            var tail = ComputeMissingTail(sym, tfEnum, acc.Last, acc.BackfillFrom, now);

            string status;
            if (acc.InWatchlist && !acc.Enabled) status = "disabled";
            else if (acc.BarCount == 0) status = "missing";
            else if (tail is not null) status = "stale";
            else status = "up-to-date";

            result.Add(new CoverageRow(
                sym, tf, acc.BarCount, acc.First, acc.Last, status,
                acc.InWatchlist, acc.Enabled, acc.BackfillFrom,
                tail?.From, tail?.To));
        }

        return result
            .OrderBy(r => r.Symbol, StringComparer.Ordinal)
            .ThenBy(r => r.Timeframe, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The [from,to] range that must be fetched to bring a cell current, or null if it is up to date.
    /// Nothing present → full backfill from the watchlist floor. Otherwise, if the last bar lags the
    /// market-hours-aware "expected latest" by more than the tolerance, fetch [lastBar, now].
    /// </summary>
    public (DateTime From, DateTime To)? ComputeMissingTail(
        string symbol, Timeframe tf, DateTime? lastBarUtc, DateTime backfillFromUtc, DateTime nowUtc)
    {
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        if (lastBarUtc is null)
        {
            return (DateTime.SpecifyKind(backfillFromUtc, DateTimeKind.Utc), now);
        }

        var last = DateTime.SpecifyKind(lastBarUtc.Value, DateTimeKind.Utc);
        var expected = MarketHours.ExpectedLatestUtc(symbol, now);
        var lag = expected - last;
        if (lag > MarketHours.Interval(tf) * StaleToleranceBars)
        {
            return (last, now);
        }
        return null;
    }

    private sealed class Accum
    {
        public long BarCount;
        public DateTime? First;
        public DateTime? Last;
        public bool InWatchlist;
        public bool Enabled = true;
        public DateTime BackfillFrom = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
