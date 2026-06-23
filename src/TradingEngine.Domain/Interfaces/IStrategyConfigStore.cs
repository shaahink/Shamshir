namespace TradingEngine.Domain;

public interface IStrategyConfigStore
{
    Task<IReadOnlyList<StrategyConfigEntry>> GetAllAsync(CancellationToken ct);
    Task UpsertAsync(StrategyConfigEntry entry, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}
