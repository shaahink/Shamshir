using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public interface IRiskProfileStore
{
    Task<IReadOnlyList<RiskProfile>> GetAllAsync(CancellationToken ct);
    Task<RiskProfile?> GetByIdAsync(string id, CancellationToken ct);
    Task UpsertAsync(RiskProfile profile, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}
