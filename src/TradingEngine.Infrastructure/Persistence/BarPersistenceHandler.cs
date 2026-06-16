using TradingEngine.Domain;
using TradingEngine.Infrastructure.Caching;

namespace TradingEngine.Infrastructure.Persistence;

public sealed class BarPersistenceHandler : IEventHandler<BarIngested>
{
    private readonly BufferedBarWriter _writer;

    public BarPersistenceHandler(BufferedBarWriter writer)
    {
        _writer = writer;
    }

    public Task HandleAsync(BarIngested evt, CancellationToken ct)
    {
        _writer.Enqueue(evt.RunId, evt.Bar);
        return Task.CompletedTask;
    }
}
