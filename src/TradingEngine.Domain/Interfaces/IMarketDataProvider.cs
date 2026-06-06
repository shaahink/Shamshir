namespace TradingEngine.Domain;

public interface IMarketDataProvider
{
    IAsyncEnumerable<Tick> StreamTicksAsync(Symbol symbol, CancellationToken ct);
    IAsyncEnumerable<Bar> StreamBarsAsync(Symbol symbol, Timeframe tf, CancellationToken ct);
    Task SeekAsync(DateTime from, DateTime to, CancellationToken ct);
}
