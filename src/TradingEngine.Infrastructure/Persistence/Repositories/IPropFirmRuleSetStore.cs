using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public interface IPropFirmRuleSetStore
{
    Task<IReadOnlyList<PropFirmRuleSet>> GetAllAsync(CancellationToken ct);
    Task<PropFirmRuleSet?> GetByIdAsync(string id, CancellationToken ct);
    Task UpsertAsync(PropFirmRuleSet ruleSet, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}
