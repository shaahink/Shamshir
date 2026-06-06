namespace TradingEngine.Domain;

public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct) where TEvent : EngineEvent;
    void Subscribe<TEvent>(IEventHandler<TEvent> handler) where TEvent : EngineEvent;
}
