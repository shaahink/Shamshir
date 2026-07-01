using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.MarketData.Providers;

/// <summary>
/// A pluggable historical-data source that ingests already-downloaded NDJSON shards from a folder
/// (iter-marketdata-tape P2 / D5). Use it to bring in third-party/vendor exports without cTrader — drop
/// shards in <paramref name="dropDir"/> and ingest. Also the concrete proof that the
/// <see cref="IHistoricalDataProvider"/> seam works end-to-end without any cTrader dependency.
/// </summary>
public sealed class FileDropProvider(MarketDataIngester ingester, string dropDir) : IHistoricalDataProvider
{
    public string Source => "filedrop";

    public async Task<HistoricalDownloadResult> DownloadAsync(HistoricalDownloadRequest request, CancellationToken ct = default)
    {
        var files = Directory.Exists(dropDir)
            ? Directory.EnumerateFiles(dropDir, "*.ndjson").OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();

        var result = await ingester.IngestDirectoryAsync(dropDir, Source, ct);
        return new HistoricalDownloadResult(result.BarsInserted, files);
    }
}
