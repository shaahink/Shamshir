namespace TradingEngine.Host;

public sealed class DataFeedService(
    IMarketDataProvider marketData,
    SimulatedBrokerAdapter simulatedBroker,
    ILogger<DataFeedService> logger) : BackgroundService
{
    private readonly TaskCompletionSource _feedComplete = new();

    public Task FeedComplete => _feedComplete.Task;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            var symbol = Symbol.Parse("EURUSD");
            logger.LogInformation("DataFeedService started for symbol {Symbol}", symbol);

            await foreach (var bar in marketData.StreamBarsAsync(symbol, Timeframe.H1, ct))
            {
                await simulatedBroker.BarWriter.WriteAsync(bar, ct);

                var barDuration = TimeSpan.FromHours(1);
                var quarter = TimeSpan.FromTicks(barDuration.Ticks / 4);
                var halfSpread = 0.0001m;

                var ticks = new[]
                {
                    new Tick(symbol, bar.Open, bar.Open + halfSpread, bar.OpenTimeUtc),
                    new Tick(symbol, bar.High, bar.High + halfSpread, bar.OpenTimeUtc + quarter),
                    new Tick(symbol, bar.Low, bar.Low + halfSpread, bar.OpenTimeUtc + 2 * quarter),
                    new Tick(symbol, bar.Close, bar.Close + halfSpread, bar.OpenTimeUtc + 3 * quarter),
                };

                foreach (var tick in ticks)
                    await simulatedBroker.TickWriter.WriteAsync(tick, ct);

                await simulatedBroker.AccountWriter.WriteAsync(
                    new AccountUpdate(100_000, 100_000, 0, ticks[^1].TimestampUtc), ct);
            }

            logger.LogInformation("DataFeedService completed — data stream exhausted");
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("DataFeedService cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DataFeedService failed");
        }
        finally
        {
            simulatedBroker.TickWriter.Complete();
            simulatedBroker.BarWriter.Complete();
            simulatedBroker.AccountWriter.Complete();
            _feedComplete.TrySetResult();
        }
    }
}
