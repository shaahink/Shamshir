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

    /// <summary>
    /// P2.6 (D9, units doctrine) gate: a flat <c>MaxPips</c> cap is silently wrong for gold (its natural
    /// ATR dwarfs a forex-calibrated cap). When a strategy config carries a normalized
    /// <c>MaxSlAtrMultiple</c>, binding the SAME strategy to XAUUSD must produce a much wider resolved
    /// <c>MaxPips</c> than binding it to EURUSD — proving the resolution is symbol-aware, not a global
    /// constant baked in once.
    /// </summary>
    [Fact]
    public void CreateStrategies_MaxSlAtrMultipleConfigured_ResolvesDifferentMaxPips_PerSymbol()
    {
        var eurUsd = new SymbolInfo(new Symbol("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            PipSize: 0.0001m, TickSize: 0.00001m, ContractSize: 100000, MinLots: 0.01m, MaxLots: 100m,
            LotStep: 0.01m, MarginRate: 0.03333m, TypicalSpread: 0.0001m);
        var xauUsd = new SymbolInfo(new Symbol("XAUUSD"), SymbolCategory.Metal, "XAU", "USD",
            PipSize: 0.01m, TickSize: 0.001m, ContractSize: 100, MinLots: 0.01m, MaxLots: 10m,
            LotStep: 0.01m, MarginRate: 0.05m, TypicalSpread: 0.3m);

        var config = new LoadedConfig([], []);
        config.StrategyConfigs =
        [
            new StrategyConfigEntry("trend-breakout", "Trend Breakout", true, "standard", JsonDocument.Parse("{}").RootElement)
            {
                PositionManagement = new PositionManagementOptions
                {
                    StopLoss = new SlOptions { MaxPips = 100, MaxSlAtrMultiple = 5.0 },
                },
            },
        ];

        IServiceProvider ServicesFor(SymbolInfo symbol)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var registry = Substitute.For<ISymbolInfoRegistry>();
            registry.TryGet(symbol.Symbol, out Arg.Any<SymbolInfo>()).Returns(x => { x[1] = symbol; return true; });
            services.AddSingleton(registry);
            return services.BuildServiceProvider();
        }

        var eurStrategy = new StrategyRegistry().CreateStrategies(
            ["trend-breakout"], config, new RunPlan([new RunPlanEntry("trend-breakout", "EURUSD", "H1")]), ServicesFor(eurUsd))
            .Should().ContainSingle().Which;
        var xauStrategy = new StrategyRegistry().CreateStrategies(
            ["trend-breakout"], config, new RunPlan([new RunPlanEntry("trend-breakout", "XAUUSD", "H1")]), ServicesFor(xauUsd))
            .Should().ContainSingle().Which;

        var eurMaxPips = eurStrategy.Config.PositionManagement.StopLoss.MaxPips;
        var xauMaxPips = xauStrategy.Config.PositionManagement.StopLoss.MaxPips;

        eurMaxPips.Should().Be(100.0);   // 5.0 * ReferenceAtrPips(H1, EURUSD spread=1.0 pip) = 5.0 * 20
        xauMaxPips.Should().Be(3000.0);  // 5.0 * ReferenceAtrPips(H1, XAUUSD spread=30 pips) = 5.0 * 600
        xauMaxPips.Should().BeGreaterThan(100, "a flat 100-pip cap would reject/crush every XAUUSD stop");
    }
}
