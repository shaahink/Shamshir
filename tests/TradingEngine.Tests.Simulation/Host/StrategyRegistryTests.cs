using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Host;

namespace TradingEngine.Tests.Simulation.Host;

/// <summary>
/// P1.5.3 gate: an unparseable run-plan timeframe string must fail loud, not silently bind H1.
/// Unreachable today (the UI only ever sends validated dropdown values), but the exact silent-failure
/// class this whole iteration exists to eliminate — a future caller (sweep runner, hand-edited RunRows
/// JSON, a new API surface) sending a bad TF string would otherwise get an H1 strategy instance with no
/// error, indistinguishable from a correctly-configured one.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class StrategyRegistryTests
{
    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = Substitute.For<ISymbolInfoRegistry>();
        services.AddSingleton(registry);
        return services.BuildServiceProvider();
    }

    private static LoadedConfig BuildConfig()
    {
        var config = new LoadedConfig([], []);
        config.StrategyConfigs =
        [
            new StrategyConfigEntry("trend-breakout", "Trend Breakout", true, "standard", JsonDocument.Parse("{}").RootElement),
        ];
        return config;
    }

    [Fact]
    public void CreateStrategies_UnparseableRunPlanTimeframe_Throws()
    {
        var strategyRegistry = new StrategyRegistry();
        var runPlan = new RunPlan([new RunPlanEntry("trend-breakout", "EURUSD", "bogus")]);

        var act = () => strategyRegistry.CreateStrategies(["trend-breakout"], BuildConfig(), runPlan, BuildServices());

        act.Should().Throw<InvalidOperationException>(
            "an unparseable run-plan timeframe string must fail loud, not silently bind H1");
    }

    [Fact]
    public void CreateStrategies_ValidRunPlanTimeframe_BindsCorrectly()
    {
        var strategyRegistry = new StrategyRegistry();
        var runPlan = new RunPlan([new RunPlanEntry("trend-breakout", "EURUSD", "M15")]);

        var strategies = strategyRegistry.CreateStrategies(["trend-breakout"], BuildConfig(), runPlan, BuildServices());

        strategies.Should().ContainSingle().Which.EntryTimeframe.Should().Be(Timeframe.M15);
    }
}
