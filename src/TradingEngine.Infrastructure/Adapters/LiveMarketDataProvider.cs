namespace TradingEngine.Infrastructure.Adapters;

public sealed class LiveMarketDataProvider : IMarketDataProvider
{
    public IAsyncEnumerable<Tick> StreamTicksAsync(Symbol symbol, CancellationToken ct)
        => throw new NotSupportedException(
            "LiveMarketDataProvider is not implemented until Phase 9 (cTrader adapter). " +
            "Use EngineMode.Backtest or EngineMode.Paper with SimulatedBrokerAdapter.");

    public IAsyncEnumerable<Bar> StreamBarsAsync(Symbol symbol, Timeframe tf, CancellationToken ct)
        => throw new NotSupportedException(
            "LiveMarketDataProvider is not implemented until Phase 9 (cTrader adapter).");

    public Task SeekAsync(DateTime from, DateTime to, CancellationToken ct)
        => throw new NotSupportedException(
            "LiveMarketDataProvider is not implemented until Phase 9 (cTrader adapter).");
}
