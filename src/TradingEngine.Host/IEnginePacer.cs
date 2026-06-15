namespace TradingEngine.Host;

public interface IEnginePacer
{
    Task PaceAsync(EngineRunner runner, CancellationToken ct);
}
