using System.Text.Json;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Configuration;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Web.Configuration;

public sealed class RiskProfileSeeder
{
    private readonly IRiskProfileStore _store;
    private readonly ILogger<RiskProfileSeeder> _logger;

    public RiskProfileSeeder(IRiskProfileStore store, ILogger<RiskProfileSeeder> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task SeedAsync(string solutionRoot, CancellationToken ct)
    {
        var existing = await _store.GetAllAsync(ct);
        if (existing.Count > 0) return;

        var loaded = new ConfigLoader(solutionRoot).LoadBase();
        _logger.LogInformation("Seeding {Count} risk profiles from JSON config", loaded.RiskProfiles.Count);

        foreach (var profile in loaded.RiskProfiles)
        {
            await _store.UpsertAsync(profile, ct);
        }
    }
}
