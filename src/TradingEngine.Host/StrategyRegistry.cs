using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Host;

public sealed class StrategyRegistry
{
    private readonly Dictionary<string, Type> _strategyTypes = [];

    public StrategyRegistry()
    {
        ScanAssembly(typeof(TrendBreakoutStrategy).Assembly);
    }

    private void ScanAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (!typeof(IStrategy).IsAssignableFrom(type) || type.IsAbstract)
                continue;

            var attr = type.GetCustomAttribute<StrategyIdAttribute>();
            if (attr is not null)
            {
                _strategyTypes[attr.Id] = type;
            }
        }
    }

    public IReadOnlyList<IStrategy> CreateStrategies(
        IReadOnlyList<string> activeIds,
        LoadedConfig config,
        IServiceProvider services)
    {
        var strategies = new List<IStrategy>();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();

        foreach (var id in activeIds)
        {
            if (!_strategyTypes.TryGetValue(id, out var type))
            {
                throw new InvalidOperationException(
                    $"Active strategy ID '{id}' has no matching [StrategyId] class. " +
                    $"Available: [{string.Join(", ", _strategyTypes.Keys)}]");
            }

            var configEntry = config.StrategyConfigs.FirstOrDefault(s => s.Id == id);
            if (configEntry is null)
            {
                throw new InvalidOperationException(
                    $"Strategy '{id}' has no config file in config/strategies/.");
            }

            var trendConfig = new TrendBreakoutConfig
            {
                Id = configEntry.Id,
                DisplayName = configEntry.DisplayName,
                Enabled = configEntry.Enabled,
                Symbols = configEntry.Symbols.ToList(),
                RiskProfileId = configEntry.RiskProfileId,
                Parameters = configEntry.Parameters,
            };

            var indicators = services.GetRequiredService<IIndicatorService>();
            var logger = loggerFactory.CreateLogger(type);
            var strategy = (IStrategy)Activator.CreateInstance(type, trendConfig, indicators, logger)!;
            strategies.Add(strategy);
        }

        return strategies;
    }
}
