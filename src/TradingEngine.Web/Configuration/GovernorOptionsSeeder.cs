using TradingEngine.Domain;
using TradingEngine.Infrastructure.Configuration;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Web.Configuration;

public sealed class GovernorOptionsSeeder
{
    private readonly IGovernorOptionsStore _store;
    private readonly ILogger<GovernorOptionsSeeder> _logger;

    public GovernorOptionsSeeder(IGovernorOptionsStore store, ILogger<GovernorOptionsSeeder> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task SeedAsync(string solutionRoot, CancellationToken ct)
    {
        var loaded = new ConfigLoader(solutionRoot).LoadBase();
        _logger.LogInformation("Seeding governor options from JSON config");

        await _store.UpsertAsync(loaded.Governor, ct);
    }
}
