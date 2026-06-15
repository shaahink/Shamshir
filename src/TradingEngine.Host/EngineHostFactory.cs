using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Host;

public static class EngineHostFactory
{
    public static IHost Create(EngineHostOptions options)
    {
        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l
                .SetMinimumLevel(options.MinLogLevel)
                .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning))
            .ConfigureServices((_, services) =>
            {
                services.AddEngineHost(options);
            })
            .Build();
    }

    public static void WireEventHandlers(IHost host)
    {
        var eventBus = host.Services.GetRequiredService<IEventBus>();
        eventBus.Subscribe<EquityUpdated>(
            host.Services.GetRequiredService<EquityPersistenceHandler>());
        eventBus.Subscribe<TradeClosed>(
            host.Services.GetRequiredService<TradePersistenceHandler>());
        eventBus.Subscribe<BarEvaluated>(
            host.Services.GetRequiredService<BarEvaluationHandler>());
        eventBus.Subscribe<GovernorStateChanged>(
            host.Services.GetRequiredService<ProtectionLedgerPersistenceHandler>());
    }

    public static void WireRiskRules(IHost host)
    {
        var rm = host.Services.GetRequiredService<RiskManager>();
        var loaded = host.Services.GetRequiredService<LoadedConfig>();
        var activeRiskProfileId = loaded.StrategyConfigs
            .Select(c => c.RiskProfileId).FirstOrDefault() ?? "standard";
        var activeProfile = loaded.RiskProfiles.FirstOrDefault(r => r.Id == activeRiskProfileId);
        var activeRuleSetId = activeProfile?.PropFirmRuleSetId ?? "ftmo-standard";
        var ruleSet = loaded.PropFirms.FirstOrDefault(r => r.Id == activeRuleSetId);
        if (ruleSet is not null)
        {
            rm.SetActiveRuleSet(ruleSet);

            var resolvedProfile = activeProfile ?? new RiskProfile(
                "standard", "Standard", 1.0, 5.0, 10.0, 100.0, 10.0, 0.5, 0.1, 5,
                false, activeRuleSetId, LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);
            rm.SetConstraints(ConstraintSet.Resolve(resolvedProfile, ruleSet));

            var passEstimator = host.Services.GetRequiredService<IPassProbabilityEstimator>();
            var complianceSvc = new PropFirmComplianceService(
                ruleSet,
                rm,
                host.Services.GetRequiredService<IEngineClock>(),
                passEstimator);
            rm.SetComplianceService(complianceSvc);
        }

        var sizePipeline = host.Services.GetRequiredService<SizeModifierPipeline>();
        rm.SetSizePipeline(sizePipeline);
    }
}
