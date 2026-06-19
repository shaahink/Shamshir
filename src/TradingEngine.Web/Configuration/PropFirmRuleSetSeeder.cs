using TradingEngine.Domain;
using TradingEngine.Infrastructure.Configuration;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Web.Configuration;

public sealed class PropFirmRuleSetSeeder
{
    private readonly IPropFirmRuleSetStore _store;
    private readonly ILogger<PropFirmRuleSetSeeder> _logger;

    public PropFirmRuleSetSeeder(IPropFirmRuleSetStore store, ILogger<PropFirmRuleSetSeeder> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task SeedAsync(string solutionRoot, CancellationToken ct)
    {
        var existing = await _store.GetAllAsync(ct);
        if (existing.Count > 0) return;

        var loaded = new ConfigLoader(solutionRoot).LoadBase();
        _logger.LogInformation("Seeding {Count} prop-firm rule sets from JSON config", loaded.PropFirms.Count);

        foreach (var ruleSet in loaded.PropFirms)
        {
            await _store.UpsertAsync(ruleSet, ct);
        }
    }
}
