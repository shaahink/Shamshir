using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Interfaces;

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
        RunPlan runPlan,
        IServiceProvider services)
    {
        var strategies = new List<IStrategy>();

        // P1.1: when no run-plan is provided (legacy/test paths), create one instance per
        // configured strategy WITHOUT binding a symbol — all strategies match all symbols (old behaviour).
        // When a run-plan IS provided, bind each row's symbol+timeframe.
        if (runPlan.Entries.Count == 0)
        {
            foreach (var id in activeIds)
            {
                if (!_strategyTypes.ContainsKey(id) && !_factories.ContainsKey(id))
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
                        $"Strategy '{id}' has no static Create method.");
                }
            }
        }
        else
        {
            foreach (var entry in runPlan.Entries)
            {
                var id = entry.StrategyId;
                if (!_strategyTypes.ContainsKey(id) && !_factories.ContainsKey(id))
                {
                    throw new InvalidOperationException(
                        $"Run-plan row has strategy ID '{id}' with no matching [StrategyId] class. " +
                        $"Available: [{string.Join(", ", _strategyTypes.Keys)}]");
                }

                var configEntry = config.StrategyConfigs.FirstOrDefault(s => s.Id == id);
                if (configEntry is null)
                {
                    throw new InvalidOperationException(
                        $"Strategy '{id}' has no config file in config/strategies/.");
                }

                if (!Enum.TryParse<Timeframe>(entry.Timeframe, ignoreCase: true, out var tf))
                {
                    throw new InvalidOperationException(
                        $"Run-plan row for strategy '{id}' has an unparseable timeframe '{entry.Timeframe}'.");
                }

                var boundEntry = configEntry with
                {
                    Symbol = entry.Symbol,
                    EntryTimeframe = tf,
                };

                // P2.6 (D9, units doctrine): normalized-unit fields (ATR-multiple/spread-multiple/ATR-fraction)
                // resolve into the existing raw-pip fields HERE, once, with the row's real symbol+TF — the
                // only point besides the per-proposal RiskProfile resolve (BarEvaluator) where both are known
                // together. Symbol/TF are fixed for the lifetime of this instance (D1 instance-per-row), so a
                // one-time resolution is correct — no per-bar re-resolution needed.
                var symbolRegistry = services.GetRequiredService<ISymbolInfoRegistry>();
                var referenceScales = services.GetService<IReferenceScaleLookup>();
                if (symbolRegistry.TryGet(new Symbol(entry.Symbol), out var symbolInfo))
                {
                    boundEntry = boundEntry with
                    {
                        PositionManagement = (boundEntry.PositionManagement ?? new()).ResolvePips(tf, symbolInfo, referenceScales),
                        OrderEntry = (boundEntry.OrderEntry ?? new()).ResolvePips(tf, symbolInfo, referenceScales),
                    };
                }

                // P4.5.4: if a calibration row exists for this (strategy, symbol, TF), override SL/TP
                // options with the calibrated values. This is the bind-time consumption path that was
                // missing — previously "Save Calibration" was a write-only table.
                var calLookup = services.GetService<IExitCalibrationLookup>();
                if (calLookup is not null)
                {
                    var cal = calLookup.Get(id, entry.Symbol, tf, null);
                    if (cal is not null)
                    {
                        var pm = boundEntry.PositionManagement ?? new();
                        boundEntry = boundEntry with
                        {
                            PositionManagement = pm with
                            {
                                StopLoss = pm.StopLoss with { Method = "AtrMultiple", AtrMultiple = cal.SlAtrMultiple },
                                TakeProfit = cal.TpRrMultiple is { } tpRr
                                    ? pm.TakeProfit with { Method = "RrMultiple", RrMultiple = tpRr }
                                    : pm.TakeProfit with { Method = "None" },
                            },
                        };
                    }
                }

                if (_factories.TryGetValue(id, out var factory))
                {
                    var strategy = factory(boundEntry, services);
                    strategies.Add(strategy);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Strategy '{id}' has no static Create method.");
                }
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
