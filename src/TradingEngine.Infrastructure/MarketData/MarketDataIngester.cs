using Microsoft.Extensions.Logging;
using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.MarketData;

public sealed class MarketDataIngester(IMarketDataStore store, ILogger<MarketDataIngester>? logger = null)
{
    public async Task<IngestResult> IngestDirectoryAsync(string dir, string source, CancellationToken ct = default,
        IProgress<IngestProgress>? progress = null)
    {
        if (!Directory.Exists(dir)) return new IngestResult(0, 0, 0, 0);

        int files = 0, lines = 0, inserted = 0, errors = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.ndjson").OrderBy(f => f, StringComparer.Ordinal))
        {
            var r = await IngestFileAsync(file, source, ct, progress);
            files++;
            lines += r.LinesRead;
            inserted += r.BarsInserted;
            errors += r.ParseErrors;
            progress?.Report(new IngestProgress(Path.GetFileName(file), files, r.LinesRead, r.BarsInserted, null));
        }
        return new IngestResult(files, lines, inserted, errors);
    }

    private const int StreamThreshold = 100_000;

    public async Task<IngestResult> IngestFileAsync(string file, string source, CancellationToken ct = default,
        IProgress<IngestProgress>? progress = null)
    {
        var lines = 0;
        var errors = 0;
        var totalInserted = 0;
        var fileName = Path.GetFileName(file);

        var storeProgress = progress is not null
            ? new Progress<int>(p => progress.Report(new IngestProgress(fileName, 1, lines, totalInserted + p, null)))
            : null;

        var fileInfo = new FileInfo(file);
        if (fileInfo.Exists && fileInfo.Length < StreamThreshold * 200)
        {
            var all = await ReadAndParseLinesAsync(file, ct);
            lines = all.lines;
            errors = all.errors;
            totalInserted = all.bars.Count > 0
                ? await store.WriteBarsAsync(source, all.bars, ct, storeProgress)
                : 0;
        }
        else
        {
            var chunk = new List<Bar>(50_000);
            var chunkIndex = 0;
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
                    chunkIndex++;
                    totalInserted += await store.WriteBarsAsync(source, chunk, ct, storeProgress);
                    chunk.Clear();
                    progress?.Report(new IngestProgress(fileName, 1, lines, totalInserted,
                        $"{lines:N0} lines, {totalInserted:N0} bars"));
                }
            }
            if (chunk.Count > 0)
                totalInserted += await store.WriteBarsAsync(source, chunk, ct, storeProgress);
        }

        logger?.LogInformation("Ingested {File}: {Lines} lines -> {Inserted} new bars, {Errors} parse errors",
            fileName, lines, totalInserted, errors);
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

public sealed record IngestProgress(
    string FileName,
    int FilesProcessed,
    int LinesRead,
    int BarsInserted,
    string? Detail);
