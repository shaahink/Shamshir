namespace TradingEngine.Infrastructure.Events;

public sealed class TypedEventBus : IEventBus
{
    private readonly Dictionary<Type, List<object>> _handlers = new();

    public void Subscribe<TEvent>(IEventHandler<TEvent> handler) where TEvent : EngineEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            _handlers[typeof(TEvent)] = list = new();
        list.Add(handler);
    }

    public async Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct) where TEvent : EngineEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers)) return;
        foreach (var h in handlers)
            await ((IEventHandler<TEvent>)h).HandleAsync(evt, ct);
    }
}
