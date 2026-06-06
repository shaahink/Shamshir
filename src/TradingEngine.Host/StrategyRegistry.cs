using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Strategies.EmaAlignment;
using TradingEngine.Strategies.MeanReversion;
using TradingEngine.Strategies.SessionBreakout;

namespace TradingEngine.Host;

public sealed class StrategyRegistry
{
    private readonly Dictionary<string, Type> _strategyTypes = [];
    private readonly Dictionary<string, Func<StrategyConfigEntry, IServiceProvider, IStrategy>> _factories = [];

    public StrategyRegistry()
    {
        ScanAssembly(typeof(TrendBreakoutStrategy).Assembly);
        RegisterFactories();
    }

    private void RegisterFactories()
    {
        _factories["trend-breakout"] = (entry, sp) =>
        {
            var config = new TrendBreakoutConfig
            {
                Id = entry.Id,
                DisplayName = entry.DisplayName,
                Enabled = entry.Enabled,
                Symbols = entry.Symbols.ToList(),
                RiskProfileId = entry.RiskProfileId,
                Timeframe = Enum.Parse<Timeframe>(entry.Timeframe, true),
                Parameters = DeserializeParams<TrendBreakoutParameters>(entry.Parameters),
            };
            var registry = sp.GetRequiredService<ISymbolInfoRegistry>();
            var logger = sp.GetRequiredService<ILogger<TrendBreakoutStrategy>>();
            return new TrendBreakoutStrategy(config, registry, logger);
        };

        _factories["ema-alignment"] = (entry, sp) =>
        {
            var config = new EmaAlignmentConfig(
                entry.Id,
                entry.DisplayName,
                entry.Symbols.ToList(),
                entry.RiskProfileId,
                DeserializeParams<EmaAlignmentParameters>(entry.Parameters),
                Enum.Parse<Timeframe>(entry.Timeframe, true));
            return new EmaAlignmentStrategy(config, sp.GetRequiredService<ILogger<EmaAlignmentStrategy>>());
        };

        _factories["mean-reversion"] = (entry, sp) =>
        {
            var config = new MeanReversionConfig(
                entry.Id,
                entry.DisplayName,
                entry.Symbols.ToList(),
                entry.RiskProfileId,
                DeserializeParams<MeanReversionParameters>(entry.Parameters),
                Enum.Parse<Timeframe>(entry.Timeframe, true));
            return new MeanReversionStrategy(config, sp.GetRequiredService<ILogger<MeanReversionStrategy>>());
        };

        _factories["session-breakout"] = (entry, sp) =>
        {
            var config = new SessionBreakoutConfig(
                entry.Id,
                entry.DisplayName,
                entry.Symbols.ToList(),
                entry.RiskProfileId,
                DeserializeParams<SessionBreakoutParameters>(entry.Parameters),
                Enum.Parse<Timeframe>(entry.Timeframe, true));
            return new SessionBreakoutStrategy(config, sp.GetRequiredService<ILogger<SessionBreakoutStrategy>>());
        };
    }

    private static T DeserializeParams<T>(JsonElement element) where T : new()
    {
        if (element.ValueKind == JsonValueKind.Undefined) return new T();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        return JsonSerializer.Deserialize<T>(element.GetRawText(), opts) ?? new T();
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
                var symbolRegistry = services.GetRequiredService<ISymbolInfoRegistry>();
                var loggerFactory = services.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger(type!);
                var strategy = (IStrategy)Activator.CreateInstance(type!, configEntry, symbolRegistry, logger)!;
                strategies.Add(strategy);
            }
        }

        return strategies;
    }
}
