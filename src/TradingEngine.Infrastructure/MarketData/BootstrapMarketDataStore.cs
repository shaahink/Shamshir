using System.Collections.Concurrent;
using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.MarketData;

public sealed class BootstrapMarketDataStore : IMarketDataStore
{
    private readonly IMarketDataStore _inner;
    private readonly ConcurrentDictionary<string, IReadOnlyList<Bar>> _bootstrapBars = new();

    // X0 (concurrent run starts): GetInventoryAsync is a full-table GROUP BY scan (~10s against the
    // real 1.2GB MarketDataBars table) that every RunsController.Start call re-runs to validate data
    // coverage. Serialized starts hid the cost; once X0 removed the one-run-at-a-time guard, N
    // concurrent starts fired N parallel scans and stacked up into request-timeout 500s (found live
    // in the X0 smoke test). Coalesce concurrent callers onto one in-flight query and cache.
    // V2 backfill scale (35M rows / 8.2GB): the scan is now ~2 minutes, so a short TTL made every
    // run submission re-pay it (timed out the V2 pilot's second POST). Writes and deletes through
    // this store invalidate the cache immediately, so the long TTL only bounds staleness against
    // OUT-OF-PROCESS importers — which the one-writer doctrine forbids while the app is serving runs.
    private readonly object _inventoryLock = new();
    private Task<IReadOnlyList<MarketDataInventoryEntry>>? _inventoryTask;
    private DateTime _inventoryCachedAtUtc;
    private static readonly TimeSpan InventoryCacheTtl = TimeSpan.FromMinutes(60);

    public BootstrapMarketDataStore(IMarketDataStore inner)
    {
        _inner = inner;
    }

    public async Task<int> WriteBarsAsync(string source, IReadOnlyList<Bar> bars, CancellationToken ct = default, IProgress<int>? progress = null)
    {
        if (source.StartsWith("bootstrap-", StringComparison.Ordinal))
        {
            _bootstrapBars[source] = bars;
            return bars.Count;
        }
        var written = await _inner.WriteBarsAsync(source, bars, ct, progress);
        InvalidateInventory();
        return written;
    }

    private void InvalidateInventory()
    {
        lock (_inventoryLock)
        {
            _inventoryTask = null;
        }
    }

    public async Task<IReadOnlyList<Bar>> ReadBarsAsync(Symbol symbol, Timeframe tf, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var stored = await _inner.ReadBarsAsync(symbol, tf, fromUtc, toUtc, ct);

        var bootstrap = _bootstrapBars.Values
            .SelectMany(bars => bars)
            .Where(b => b.Symbol == symbol && b.Timeframe == tf && b.OpenTimeUtc >= fromUtc && b.OpenTimeUtc <= toUtc)
            .ToList();

        if (bootstrap.Count == 0)
            return stored;

        return stored.Concat(bootstrap).OrderBy(b => b.OpenTimeUtc).ToList();
    }

    public Task<IReadOnlyList<MarketDataInventoryEntry>> GetInventoryAsync(CancellationToken ct = default)
    {
        lock (_inventoryLock)
        {
            var stale = _inventoryTask is null
                || _inventoryTask.IsFaulted
                || _inventoryTask.IsCanceled
                || DateTime.UtcNow - _inventoryCachedAtUtc >= InventoryCacheTtl;

            if (stale)
            {
                // Deliberately CancellationToken.None: this task is shared across concurrent callers,
                // so one caller's request being cancelled/aborted must not cancel it for the others.
                _inventoryTask = _inner.GetInventoryAsync(CancellationToken.None);
                _inventoryCachedAtUtc = DateTime.UtcNow;
            }

            return _inventoryTask!;
        }
    }

    public Task<int> CountBarsAsync(Symbol symbol, Timeframe tf, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        => _inner.CountBarsAsync(symbol, tf, fromUtc, toUtc, ct);

    public Task<IReadOnlyList<MarketDataGap>> GetGapsAsync(Symbol symbol, Timeframe tf, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        => _inner.GetGapsAsync(symbol, tf, fromUtc, toUtc, ct);

    public async Task<int> DeleteBarsAsync(Symbol symbol, Timeframe tf, DateTime? fromUtc, DateTime? toUtc, string? source, CancellationToken ct = default)
    {
        var deleted = await _inner.DeleteBarsAsync(symbol, tf, fromUtc, toUtc, source, ct);
        InvalidateInventory();
        return deleted;
    }
}
