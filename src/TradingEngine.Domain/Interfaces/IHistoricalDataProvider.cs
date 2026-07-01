namespace TradingEngine.Domain;

/// <summary>
/// Pluggable HISTORICAL-data source (iter-marketdata-tape P2 / D5). Distinct from the live-feed
/// <see cref="IMarketDataProvider"/> (which streams ticks/bars for a running engine): this one BULK-fetches
/// history into the canonical <see cref="IMarketDataStore"/>. cTrader is the first implementation (a recorder
/// cBot), but any vendor (Dukascopy, Polygon, a manual file drop…) can implement it, so swapping to a
/// higher-quality feed later needs no change upstream.
/// </summary>
public interface IHistoricalDataProvider
{
    /// <summary>Provenance tag written onto every stored bar (e.g. "ctrader", "dukascopy", "filedrop").</summary>
    string Source { get; }

    /// <summary>Fetch/ingest the requested history into the canonical store. Idempotent (the store dedupes),
    /// so re-running a request over an already-covered window inserts nothing.</summary>
    Task<HistoricalDownloadResult> DownloadAsync(HistoricalDownloadRequest request, CancellationToken ct = default);
}

public sealed record HistoricalDownloadRequest(
    IReadOnlyList<string> Symbols,
    IReadOnlyList<Timeframe> Timeframes,
    DateTime FromUtc,
    DateTime ToUtc);

public sealed record HistoricalDownloadResult(
    int BarsInserted,
    IReadOnlyList<string> ShardFiles,
    string? Error = null)
{
    public bool Success => Error is null;
}
