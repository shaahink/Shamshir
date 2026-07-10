using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.MarketData;

public sealed class BlockBootstrapper
{
    private readonly IMarketDataStore _store;

    public BlockBootstrapper(IMarketDataStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<IReadOnlyList<Bar>>> GenerateAsync(
        Symbol symbol,
        Timeframe tf,
        DateTime from,
        DateTime to,
        TimeSpan blockSize,
        int tapeCount,
        int seed,
        CancellationToken ct = default)
    {
        if (tapeCount < 1) throw new ArgumentOutOfRangeException(nameof(tapeCount), "Must be >= 1");
        if (blockSize.Ticks <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize));
        if (from >= to) throw new ArgumentException("from must be < to");

        var bars = await _store.ReadBarsAsync(symbol, tf, from, to, ct);
        if (bars.Count == 0)
            return [];

        var blocks = PartitionIntoBlocks(bars, blockSize).ToList();
        if (blocks.Count == 0)
            return [];

        var rng = new Random(seed);
        var totalBars = bars.Count;
        var interval = tf.ToTimeSpan();
        var baseDate = new DateTime(2000, 1, 3, 0, 0, 0, DateTimeKind.Utc); // Monday

        var tapes = new List<IReadOnlyList<Bar>>(tapeCount);
        for (var t = 0; t < tapeCount; t++)
        {
            var synthetic = new List<Bar>(totalBars);
            while (synthetic.Count < totalBars)
            {
                var block = blocks[rng.Next(blocks.Count)];
                foreach (var bar in block)
                {
                    if (synthetic.Count >= totalBars) break;
                    synthetic.Add(bar);
                }
            }

            var shifted = RemapTimestamps(synthetic, baseDate, interval);
            tapes.Add(shifted);

            baseDate = baseDate.AddDays(7 * tapeCount + 1);
        }

        return tapes;
    }

    private static IEnumerable<IReadOnlyList<Bar>> PartitionIntoBlocks(IReadOnlyList<Bar> bars, TimeSpan blockSize)
    {
        var blocks = new List<List<Bar>>();
        List<Bar>? current = null;
        DateTime? blockEnd = null;

        foreach (var bar in bars)
        {
            if (blockEnd is null || bar.OpenTimeUtc >= blockEnd.Value)
            {
                current = new List<Bar>();
                blocks.Add(current);
                blockEnd = bar.OpenTimeUtc + blockSize;
            }
            current!.Add(bar);
        }

        return blocks.Select(b => (IReadOnlyList<Bar>)b.AsReadOnly()).ToList();
    }

    private static IReadOnlyList<Bar> RemapTimestamps(
        IReadOnlyList<Bar> bars,
        DateTime baseDate,
        TimeSpan interval)
    {
        var shifted = new List<Bar>(bars.Count);
        var current = baseDate;

        foreach (var bar in bars)
        {
            shifted.Add(bar with { OpenTimeUtc = current });
            current += interval;
        }

        return shifted;
    }
}
