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

    private const int StreamThreshold = 100_000;

    public async Task<IngestResult> IngestFileAsync(string file, string source, CancellationToken ct = default)
    {
        var lines = 0;
        var errors = 0;
        var totalInserted = 0;

        // For small files, parse and insert in one batch; for large files, stream in chunks.
        var fileInfo = new FileInfo(file);
        if (fileInfo.Exists && fileInfo.Length < StreamThreshold * 200) // rough: ~200 bytes/line
        {
            var all = await ReadAndParseLinesAsync(file, ct);
            lines = all.lines;
            errors = all.errors;
            totalInserted = all.bars.Count > 0 ? await store.WriteBarsAsync(source, all.bars, ct) : 0;
        }
        else
        {
            var chunk = new List<Bar>(50_000);
            foreach (var line in File.ReadLines(file))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                lines++;
                if (MarketDataShardIo.TryParse(line, out var bar))
                    chunk.Add(bar);
                else
                    errors++;

                if (chunk.Count >= 50_000)
                {
                    totalInserted += await store.WriteBarsAsync(source, chunk, ct);
                    chunk.Clear();
                }
            }
            if (chunk.Count > 0)
                totalInserted += await store.WriteBarsAsync(source, chunk, ct);
        }

        logger?.LogInformation("Ingested {File}: {Lines} lines -> {Inserted} new bars, {Errors} parse errors",
            Path.GetFileName(file), lines, totalInserted, errors);
        return new IngestResult(1, lines, totalInserted, errors);
    }

    private static async Task<(int lines, int errors, List<Bar> bars)> ReadAndParseLinesAsync(string file, CancellationToken ct)
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
        return (lines, errors, bars);
    }
}

public sealed record IngestResult(int FilesProcessed, int LinesRead, int BarsInserted, int ParseErrors);
