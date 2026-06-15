namespace TradingEngine.Tests.Simulation.Harness;

public sealed class CollectingEventBus : IEventBus
{
    private readonly List<(EngineEvent Event, DateTime Timestamp)> _events = [];
    private readonly Dictionary<Type, List<object>> _handlers = [];

    public IReadOnlyList<(EngineEvent Event, DateTime Timestamp)> Events => _events;

    public IReadOnlyList<T> OfType<T>() where T : EngineEvent
        => _events.Where(e => e.Event is T).Select(e => (T)e.Event).ToList();

    public Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct) where TEvent : EngineEvent
    {
        _events.Add((evt, evt.OccurredAtUtc));

        if (_handlers.TryGetValue(typeof(TEvent), out var handlers))
        {
            foreach (var h in handlers)
            {
                ((IEventHandler<TEvent>)h).HandleAsync(evt, ct);
            }
        }

        return Task.CompletedTask;
    }

    public void Subscribe<TEvent>(IEventHandler<TEvent> handler) where TEvent : EngineEvent
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var list))
        {
            list = [];
            _handlers[typeof(TEvent)] = list;
        }

        list.Add(handler!);
    }
}
