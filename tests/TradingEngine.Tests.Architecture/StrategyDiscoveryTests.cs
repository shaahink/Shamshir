using System.Reflection;
using FluentAssertions;
using TradingEngine.Domain;
using TradingEngine.Host;

namespace TradingEngine.Tests.Architecture;

/// <summary>
/// iter-strategy-system P0: the StrategyRegistry discovers strategies by reflection on the
/// <see cref="StrategyIdAttribute"/>. iter-38 replaced the old hardcoded factory lambdas with this
/// reflection scan but 3 of the 9 strategy classes (ema-alignment, mean-reversion, session-breakout)
/// were left without the attribute, so they silently dropped out of discovery and threw
/// "Active strategy ID '…' has no matching [StrategyId] class" at engine start. These tests pin the
/// invariant so a strategy class that forgets the attribute (or the static Create factory) fails the
/// gate instead of shipping un-runnable.
/// </summary>
public sealed class StrategyDiscoveryTests
{
    private static readonly Assembly StrategiesAssembly =
        typeof(TradingEngine.Strategies.TrendBreakout.TrendBreakoutStrategy).Assembly;

    private static IEnumerable<Type> ConcreteStrategyTypes() =>
        StrategiesAssembly.GetTypes()
            .Where(t => typeof(IStrategy).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

    [Fact]
    public void Every_concrete_strategy_has_a_StrategyId_attribute()
    {
        var missing = ConcreteStrategyTypes()
            .Where(t => t.GetCustomAttribute<StrategyIdAttribute>() is null)
            .Select(t => t.Name)
            .ToList();

        missing.Should().BeEmpty(
            "every IStrategy implementation must carry [StrategyId] or it is invisible to the registry. Missing: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void Every_concrete_strategy_has_a_static_Create_factory()
    {
        var missing = ConcreteStrategyTypes()
            .Where(t => t.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null,
                [typeof(StrategyConfigEntry), typeof(IServiceProvider)], null) is null)
            .Select(t => t.Name)
            .ToList();

        missing.Should().BeEmpty(
            "every strategy needs a public static Create(StrategyConfigEntry, IServiceProvider). Missing: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void Registry_discovers_all_nine_strategies_including_the_three_that_regressed()
    {
        var ids = new StrategyRegistry().GetAllIds();

        ids.Should().Contain(new[]
        {
            "trend-breakout", "super-trend", "rsi-divergence", "mtf-trend", "macd-momentum",
            "bb-squeeze", "ema-alignment", "mean-reversion", "session-breakout",
        });
        ids.Should().HaveCount(ConcreteStrategyTypes().Count());
    }
}
