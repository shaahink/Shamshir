using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Strategies.BollingerSqueeze;
using TradingEngine.Strategies.EmaAlignment;
using TradingEngine.Strategies.MacdMomentum;
using TradingEngine.Strategies.MeanReversion;
using TradingEngine.Strategies.MtfTrend;
using TradingEngine.Strategies.RsiDivergence;
using TradingEngine.Strategies.SessionBreakout;
using TradingEngine.Strategies.SuperTrend;

namespace TradingEngine.Host;

public sealed class StrategyRegistry
{
    private readonly Dictionary<string, Type> _strategyTypes = [];
    private readonly Dictionary<string, Func<StrategyConfigEntry, IServiceProvider, IStrategy>> _factories = [];
    private IReadOnlyList<IStrategy>? _cachedAll;

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
                RegimeFilter = entry.RegimeFilter ?? new(),
                OrderEntry = entry.OrderEntry ?? new(),
                PositionManagement = entry.PositionManagement ?? new(),
                Parameters = DeserializeParams<TrendBreakoutParameters>(entry.Parameters),
            };
            var registry = sp.GetRequiredService<ISymbolInfoRegistry>();
            var logger = sp.GetRequiredService<ILogger<TrendBreakoutStrategy>>();
            return new TrendBreakoutStrategy(config, registry, logger);
        };

        _factories["ema-alignment"] = (entry, sp) =>
        {
            var config = new EmaAlignmentConfig(
                entry.Id, entry.DisplayName, entry.Enabled,
                entry.Symbols.ToList(), entry.RiskProfileId,
                DeserializeParams<EmaAlignmentParameters>(entry.Parameters),
                Enum.Parse<Timeframe>(entry.Timeframe, true))
            {
                RegimeFilter = entry.RegimeFilter ?? new(),
                OrderEntry = entry.OrderEntry ?? new(),
                PositionManagement = entry.PositionManagement ?? new(),
            };
            return new EmaAlignmentStrategy(config, sp.GetRequiredService<ISymbolInfoRegistry>(), sp.GetRequiredService<ILogger<EmaAlignmentStrategy>>());
        };

        _factories["mean-reversion"] = (entry, sp) =>
        {
            var config = new MeanReversionConfig(
                entry.Id, entry.DisplayName, entry.Enabled,
                entry.Symbols.ToList(), entry.RiskProfileId,
                DeserializeParams<MeanReversionParameters>(entry.Parameters),
                Enum.Parse<Timeframe>(entry.Timeframe, true))
            {
                RegimeFilter = entry.RegimeFilter ?? new(),
                OrderEntry = entry.OrderEntry ?? new(),
                PositionManagement = entry.PositionManagement ?? new(),
            };
            return new MeanReversionStrategy(config, sp.GetRequiredService<ISymbolInfoRegistry>(), sp.GetRequiredService<ILogger<MeanReversionStrategy>>());
        };

        _factories["session-breakout"] = (entry, sp) =>
        {
            var config = new SessionBreakoutConfig(
                entry.Id, entry.DisplayName, entry.Enabled,
                entry.Symbols.ToList(), entry.RiskProfileId,
                DeserializeParams<SessionBreakoutParameters>(entry.Parameters),
                Enum.Parse<Timeframe>(entry.Timeframe, true))
            {
                RegimeFilter = entry.RegimeFilter ?? new(),
                OrderEntry = entry.OrderEntry ?? new(),
                PositionManagement = entry.PositionManagement ?? new(),
            };
            return new SessionBreakoutStrategy(config, sp.GetRequiredService<ISymbolInfoRegistry>(), sp.GetRequiredService<ILogger<SessionBreakoutStrategy>>());
        };

        _factories["rsi-divergence"] = (entry, sp) =>
        {
            var config = new RsiDivergenceConfig
            {
                Id = entry.Id, DisplayName = entry.DisplayName, Enabled = entry.Enabled,
                Symbols = entry.Symbols.ToList(), RiskProfileId = entry.RiskProfileId,
                Timeframe = Enum.Parse<Timeframe>(entry.Timeframe, true),
                RegimeFilter = entry.RegimeFilter ?? new(),
                OrderEntry = entry.OrderEntry ?? new(),
                PositionManagement = entry.PositionManagement ?? new(),
                Parameters = DeserializeParams<RsiDivergenceParameters>(entry.Parameters),
            };
            var registry = sp.GetRequiredService<ISymbolInfoRegistry>();
            var logger = sp.GetRequiredService<ILogger<RsiDivergenceStrategy>>();
            return new RsiDivergenceStrategy(config, registry, logger);
        };

        _factories["bb-squeeze"] = (entry, sp) =>
        {
            var config = new BollingerSqueezeConfig
            {
                Id = entry.Id, DisplayName = entry.DisplayName, Enabled = entry.Enabled,
                Symbols = entry.Symbols.ToList(), RiskProfileId = entry.RiskProfileId,
                Timeframe = Enum.Parse<Timeframe>(entry.Timeframe, true),
                RegimeFilter = entry.RegimeFilter ?? new(),
                OrderEntry = entry.OrderEntry ?? new(),
                PositionManagement = entry.PositionManagement ?? new(),
                Parameters = DeserializeParams<BollingerSqueezeParameters>(entry.Parameters),
            };
            var registry = sp.GetRequiredService<ISymbolInfoRegistry>();
            var logger = sp.GetRequiredService<ILogger<BollingerSqueezeStrategy>>();
            return new BollingerSqueezeStrategy(config, registry, logger);
        };

        _factories["macd-momentum"] = (entry, sp) =>
        {
            var config = new MacdMomentumConfig
            {
                Id = entry.Id, DisplayName = entry.DisplayName, Enabled = entry.Enabled,
                Symbols = entry.Symbols.ToList(), RiskProfileId = entry.RiskProfileId,
                Timeframe = Enum.Parse<Timeframe>(entry.Timeframe, true),
                RegimeFilter = entry.RegimeFilter ?? new(),
                OrderEntry = entry.OrderEntry ?? new(),
                PositionManagement = entry.PositionManagement ?? new(),
                Parameters = DeserializeParams<MacdMomentumParameters>(entry.Parameters),
            };
            var registry = sp.GetRequiredService<ISymbolInfoRegistry>();
            var logger = sp.GetRequiredService<ILogger<MacdMomentumStrategy>>();
            return new MacdMomentumStrategy(config, registry, logger);
        };

        _factories["mtf-trend"] = (entry, sp) =>
        {
            var config = new MtfTrendConfig
            {
                Id = entry.Id, DisplayName = entry.DisplayName, Enabled = entry.Enabled,
                Symbols = entry.Symbols.ToList(), RiskProfileId = entry.RiskProfileId,
                Timeframe = Enum.Parse<Timeframe>(entry.Timeframe, true),
                RegimeFilter = entry.RegimeFilter ?? new(),
                OrderEntry = entry.OrderEntry ?? new(),
                PositionManagement = entry.PositionManagement ?? new(),
                Parameters = DeserializeParams<MtfTrendParameters>(entry.Parameters),
            };
            var registry = sp.GetRequiredService<ISymbolInfoRegistry>();
            var logger = sp.GetRequiredService<ILogger<MtfTrendStrategy>>();
            return new MtfTrendStrategy(config, registry, logger);
        };

        _factories["super-trend"] = (entry, sp) =>
        {
            var config = new SuperTrendConfig
            {
                Id = entry.Id, DisplayName = entry.DisplayName, Enabled = entry.Enabled,
                Symbols = entry.Symbols.ToList(), RiskProfileId = entry.RiskProfileId,
                Timeframe = Enum.Parse<Timeframe>(entry.Timeframe, true),
                RegimeFilter = entry.RegimeFilter ?? new(),
                OrderEntry = entry.OrderEntry ?? new(),
                PositionManagement = entry.PositionManagement ?? new(),
                Parameters = DeserializeParams<SuperTrendParameters>(entry.Parameters),
            };
            var registry = sp.GetRequiredService<ISymbolInfoRegistry>();
            var logger = sp.GetRequiredService<ILogger<SuperTrendStrategy>>();
            return new SuperTrendStrategy(config, registry, logger);
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

        _cachedAll = strategies;
        return strategies;
    }

    public IReadOnlyList<IStrategy> GetAll()
        => _cachedAll ?? [];

    public IReadOnlyList<string> GetAllIds()
        => _strategyTypes.Keys.OrderBy(k => k).ToList();

    /// <summary>
    /// Resolve which strategy IDs a run should instantiate. Empty <paramref name="selectedIds"/>
    /// means "all configured" (the default); otherwise the configured set is filtered to the
    /// selection, so the New-Backtest strategy picker is honoured. Selections that aren't configured
    /// are dropped (rather than throwing in <see cref="CreateStrategies"/>). Configured order is kept.
    /// </summary>
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
