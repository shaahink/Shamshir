using Microsoft.Extensions.Logging;
using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.MarketData;

/// <summary>
/// Bulk-loads NDJSON shards (<see cref="MarketDataShardIo"/>) into the canonical <see cref="IMarketDataStore"/>
/// (iter-marketdata-tape P2). Malformed lines are counted and skipped rather than aborting the whole file, so
/// a partial/streamed shard still ingests what it can. Idempotent via the store's dedupe.
/// </summary>
public sealed class MarketDataIngester(IMarketDataStore store, ILogger<MarketDataIngester>? logger = null)
{
    public async Task<IngestResult> IngestDirectoryAsync(string dir, string source, CancellationToken ct = default)
    {
        if (!Directory.Exists(dir)) return new IngestResult(0, 0, 0, 0);

        int files = 0, lines = 0, inserted = 0, errors = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.ndjson").OrderBy(f => f, StringComparer.Ordinal))
        {
            var r = await IngestFileAsync(file, source, ct);
            files++;
            lines += r.LinesRead;
            inserted += r.BarsInserted;
            errors += r.ParseErrors;
        }
        return new IngestResult(files, lines, inserted, errors);
    }

    public async Task<IngestResult> IngestFileAsync(string file, string source, CancellationToken ct = default)
    {
        var bars = new List<Bar>();
        int lines = 0, errors = 0;
        foreach (var line in File.ReadLines(file))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            lines++;
            if (MarketDataShardIo.TryParse(line, out var bar)) bars.Add(bar);
            else errors++;
        }

        var inserted = bars.Count > 0 ? await store.WriteBarsAsync(source, bars, ct) : 0;
        logger?.LogInformation("Ingested {File}: {Lines} lines → {Inserted} new bars, {Errors} parse errors",
            Path.GetFileName(file), lines, inserted, errors);
        return new IngestResult(1, lines, inserted, errors);
    }
}

public sealed record IngestResult(int FilesProcessed, int LinesRead, int BarsInserted, int ParseErrors);
