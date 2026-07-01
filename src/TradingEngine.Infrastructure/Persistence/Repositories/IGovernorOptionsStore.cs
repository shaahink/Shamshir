using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public interface IGovernorOptionsStore
{
    Task<GovernorOptions> GetAsync(CancellationToken ct);
    Task UpsertAsync(GovernorOptions options, CancellationToken ct);
}
