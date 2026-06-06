namespace TradingEngine.Domain;

public interface IEventLogRepository
{
    Task AppendAsync(EngineEvent evt, CancellationToken ct);
    Task<IReadOnlyList<EngineEvent>> GetRecentAsync(int count, CancellationToken ct);
}
