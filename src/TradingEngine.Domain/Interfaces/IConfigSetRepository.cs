namespace TradingEngine.Domain;

public interface IConfigSetRepository
{
    Task SaveAsync(ConfigSet config, CancellationToken ct);
    Task<ConfigSet?> GetByIdAsync(string configSetId, CancellationToken ct);
    Task<IReadOnlyList<ConfigSet>> GetAllAsync(CancellationToken ct);
    Task<string?> GetContentHashAsync(string json, CancellationToken ct);
}
