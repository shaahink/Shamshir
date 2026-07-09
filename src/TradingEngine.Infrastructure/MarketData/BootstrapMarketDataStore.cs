using System.Collections.Concurrent;
using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.MarketData;

public sealed class BootstrapMarketDataStore : IMarketDataStore
{
    private readonly IMarketDataStore _inner;
    private readonly ConcurrentDictionary<string, IReadOnlyList<Bar>> _bootstrapBars = new();

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
        return await _inner.WriteBarsAsync(source, bars, ct, progress);
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
        => _inner.GetInventoryAsync(ct);

    public Task<IReadOnlyList<MarketDataGap>> GetGapsAsync(Symbol symbol, Timeframe tf, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        => _inner.GetGapsAsync(symbol, tf, fromUtc, toUtc, ct);

    public Task<int> DeleteBarsAsync(Symbol symbol, Timeframe tf, DateTime? fromUtc, DateTime? toUtc, string? source, CancellationToken ct = default)
        => _inner.DeleteBarsAsync(symbol, tf, fromUtc, toUtc, source, ct);
}
