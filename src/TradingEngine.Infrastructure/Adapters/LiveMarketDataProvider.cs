using System.Runtime.CompilerServices;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class LiveMarketDataProvider : IMarketDataProvider
{
    private readonly IBrokerAdapter _adapter;

    public LiveMarketDataProvider(IBrokerAdapter adapter)
    {
        _adapter = adapter;
    }

    public DateTime LastTickTimeUtc { get; private set; }

    public IAsyncEnumerable<Tick> StreamTicksAsync(Symbol symbol, CancellationToken ct)
        => StreamTicksAsync(symbol, ct, Timeframe.H1);

    public async IAsyncEnumerable<Tick> StreamTicksAsync(
        Symbol symbol,
        [EnumeratorCancellation] CancellationToken ct,
        Timeframe tf)
    {
        await foreach (var tick in _adapter.TickStream.ReadAllAsync(ct))
        {
            LastTickTimeUtc = tick.TimestampUtc;
            if (tick.Symbol.Equals(symbol))
                yield return tick;
        }
    }

    public async IAsyncEnumerable<Bar> StreamBarsAsync(
        Symbol symbol, Timeframe tf,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var bar in _adapter.BarStream.ReadAllAsync(ct))
        {
            if (bar.Symbol.Equals(symbol) && bar.Timeframe == tf)
                yield return bar;
        }
    }

    public Task SeekAsync(DateTime from, DateTime to, CancellationToken ct)
        => Task.CompletedTask;
}
