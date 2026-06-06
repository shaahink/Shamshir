using System.Collections.Concurrent;

namespace TradingEngine.Infrastructure.Events;

public sealed class TypedEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<object>> _handlers = new();

    public void Subscribe<TEvent>(IEventHandler<TEvent> handler) where TEvent : EngineEvent
    {
        var handlers = _handlers.GetOrAdd(typeof(TEvent), _ => []);
        lock (handlers)
        {
            handlers.Add(handler);
        }
    }

    public async Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct) where TEvent : EngineEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers)) return;
        List<object> snapshot;
        lock (handlers)
        {
            snapshot = [.. handlers];
        }
        foreach (var h in snapshot)
        {
            await ((IEventHandler<TEvent>)h).HandleAsync(evt, ct);
        }
    }
}
