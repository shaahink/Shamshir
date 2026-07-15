using TradingEngine.Domain;

namespace TradingEngine.Infrastructure;

/// <summary>
/// A DB-backed event tape that reads bars and yields <see cref="BarClosed"/> events in time order
/// (iter-35 A1). This is the production tape for backtest replay — it replaces a live data feed with
/// a deterministic recorded stream so the kernel output is reproducible.
/// </summary>
public sealed class BarTape : IEventTape
{
    private readonly IBarRepository _bars;

    public DatasetRef Dataset { get; }

    public BarTape(DatasetRef dataset, IBarRepository bars)
    {
        Dataset = dataset;
        _bars = bars;
    }

    public async IAsyncEnumerable<EngineEvent> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Read each symbol/timeframe pair in parallel-like fashion: for each symbol+tf,
        // query bars and merge them by OpenTimeUtc into a single ordered stream.
        var streams = new List<IAsyncEnumerable<BarClosed>>();

        foreach (var symStr in Dataset.Symbols)
        {
            var symbol = Symbol.Parse(symStr);
            foreach (var tfStr in Dataset.Timeframes)
            {
                var tf = Enum.Parse<Timeframe>(tfStr);
                streams.Add(ReadBarsAsEventsAsync(symbol, tf, ct));
            }
        }

        // Merge multiple streams into time-ordered single stream.
        if (streams.Count == 1)
        {
            await foreach (var evt in streams[0])
                yield return evt;
            yield break;
        }

        // Multi-stream merge via priority queue on BarOpenTimeUtc.
        var enumerators = new List<IAsyncEnumerator<BarClosed>>(streams.Count);
        try
        {
            foreach (var s in streams)
            {
                var e = s.GetAsyncEnumerator(ct);
                if (await e.MoveNextAsync())
                    enumerators.Add(e);
            }

            while (enumerators.Count > 0)
            {
                var earliest = 0;
                for (var i = 1; i < enumerators.Count; i++)
                {
                    if (enumerators[i].Current.OccurredAtUtc < enumerators[earliest].Current.OccurredAtUtc)
                        earliest = i;
                }

                yield return enumerators[earliest].Current;

                if (!await enumerators[earliest].MoveNextAsync())
                {
                    enumerators.RemoveAt(earliest);
                }
            }
        }
        finally
        {
            foreach (var e in enumerators)
                await e.DisposeAsync();
        }
    }

    private async IAsyncEnumerable<BarClosed> ReadBarsAsEventsAsync(
        Symbol symbol, Timeframe tf, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var bars = await _bars.GetAsync(symbol, tf, Dataset.FromUtc, Dataset.ToUtc, ct);
        foreach (var b in bars)
        {
            yield return new BarClosed(b.Symbol, b.Timeframe, b.Open, b.High, b.Low, b.Close, b.OpenTimeUtc);
        }
    }
}
