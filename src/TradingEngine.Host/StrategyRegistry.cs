using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Host;

public sealed class StrategyRegistry
{
    private readonly Dictionary<string, Type> _strategyTypes = [];
    private readonly Dictionary<string, Func<StrategyConfigEntry, IServiceProvider, IStrategy>> _factories = [];
    private IReadOnlyList<IStrategy>? _cachedAll;

    public StrategyRegistry()
    {
        ScanAssembly(typeof(TradingEngine.Strategies.TrendBreakout.TrendBreakoutStrategy).Assembly);
        DiscoverFactories();
    }

    private void DiscoverFactories()
    {
        foreach (var (id, type) in _strategyTypes)
        {
            var createMethod = type.GetMethod("Create",
                BindingFlags.Public | BindingFlags.Static,
                null,
                [typeof(StrategyConfigEntry), typeof(IServiceProvider)],
                null);
            if (createMethod is not null)
            {
                _factories[id] = (entry, sp) =>
                    (IStrategy)createMethod.Invoke(null, [entry, sp])!;
            }
        }
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

        foreach (var id in activeIds)
        {
            if (!_strategyTypes.TryGetValue(id, out var type) && !_factories.ContainsKey(id))
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

            if (_factories.TryGetValue(id, out var factory))
            {
                var strategy = factory(configEntry, services);
                strategies.Add(strategy);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Strategy '{id}' has no static Create method. Add a public static Create(StrategyConfigEntry, IServiceProvider) method.");
            }
        }

        _cachedAll = strategies;
        return strategies;
    }

    public IReadOnlyList<IStrategy> GetAll()
        => _cachedAll ?? [];

    public IReadOnlyList<string> GetAllIds()
        => _strategyTypes.Keys.OrderBy(k => k).ToList();

    public static IReadOnlyList<string> SelectActiveIds(
        IEnumerable<string> configuredIds, IReadOnlyList<string> selectedIds)
    {
        var configured = configuredIds.ToArray();
        if (selectedIds is not { Count: > 0 })
            return configured;
        var selected = new HashSet<string>(selectedIds, StringComparer.Ordinal);
        return configured.Where(selected.Contains).ToArray();
    }
}
