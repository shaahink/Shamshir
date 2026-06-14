namespace TradingEngine.Domain;

public interface IEffectExecutor
{
    Task ExecuteAsync(EngineEffect effect, CancellationToken ct);
}
