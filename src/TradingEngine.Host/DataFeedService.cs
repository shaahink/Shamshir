namespace TradingEngine.Host;

public sealed class DataFeedService(
    IMarketDataProvider marketData,
    IBrokerAdapter broker,
    ILogger<DataFeedService> logger) : BackgroundService
{
    public IReadOnlyList<Symbol> Symbols { get; init; } = [Symbol.Parse("EURUSD")];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            foreach (var symbol in Symbols)
            {
                logger.LogInformation("Data feed started for {Symbol}", symbol);

                var barTask = FeedBarsAsync(symbol, ct);
                var tickTask = FeedTicksAsync(symbol, ct);

                await Task.WhenAll(barTask, tickTask);
            }

            logger.LogInformation("Data feed completed");
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Data feed cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Data feed failed");
        }
        finally
        {
            if (broker is SimulatedBrokerAdapter sim)
            {
                sim.TickWriter.Complete();
                sim.BarWriter.Complete();
                sim.AccountWriter.Complete();
            }
        }
    }

    private async Task FeedBarsAsync(Symbol symbol, CancellationToken ct)
    {
        await foreach (var bar in marketData.StreamBarsAsync(symbol, Timeframe.H1, ct))
        {
            if (broker is SimulatedBrokerAdapter sim)
                await sim.BarWriter.WriteAsync(bar, ct);
        }
    }

    private async Task FeedTicksAsync(Symbol symbol, CancellationToken ct)
    {
        await foreach (var tick in marketData.StreamTicksAsync(symbol, ct))
        {
            if (broker is SimulatedBrokerAdapter sim)
                await sim.TickWriter.WriteAsync(tick, ct);
        }
    }
}
