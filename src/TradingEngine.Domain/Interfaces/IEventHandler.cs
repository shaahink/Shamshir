namespace TradingEngine.Domain;

public interface IEventHandler<in TEvent> where TEvent : EngineEvent
{
    Task HandleAsync(TEvent evt, CancellationToken ct);
}
