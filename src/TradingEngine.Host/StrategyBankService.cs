namespace TradingEngine.Host;

public sealed class StrategyBankService : IStrategyBank
{
    private readonly StrategyRegistry _registry;
    private readonly StrategyRotationOptions? _rotation;
    private readonly RunPlan? _runPlan;
    private readonly IReadOnlySet<string>? _runPlanEntries;
    private readonly Dictionary<string, bool> _enabledOverrides = new();
    private readonly Dictionary<string, StrategyPerformanceStats> _stats = new();
    private readonly ILogger<StrategyBankService> _logger;

    public StrategyBankService(
        StrategyRegistry registry,
        StrategyRotationOptions? rotation,
        RunPlan? runPlan,
        ILogger<StrategyBankService> logger)
    {
        _registry = registry;
        _rotation = rotation;
        _runPlan = runPlan;
        _logger = logger;

        if (runPlan is { Entries.Count: > 0 })
        {
            _runPlanEntries = runPlan.Entries
                .Select(e => $"{e.StrategyId}|{e.Symbol}|{e.Timeframe}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<IStrategy> GetActive(Symbol symbol, Timeframe timeframe, MarketRegime regime, bool ignoreRegime = false)
    {
        var timeframeStr = timeframe.ToString();
        var symbolStr = symbol.Value;

        return _registry.GetAll()
            .Where(s => _enabledOverrides.TryGetValue(s.Id, out var en) ? en : s.Config.Enabled)
            .Where(s => IsInRunPlan(s.Id, symbolStr, timeframeStr))
            .Where(s => s.EntryTimeframe == timeframe)
            .Where(s => ignoreRegime || s.Config.RegimeFilter.Allows(regime))
            .ToList();
    }

    private bool IsInRunPlan(string strategyId, string symbol, string timeframe)
    {
        if (_runPlanEntries is null)
            return true;

        return _runPlanEntries.Contains($"{strategyId}|{symbol}|{timeframe}");
    }

    public IReadOnlyList<IStrategy> GetAll() => _registry.GetAll();

    public void Enable(string strategyId)
    {
        _enabledOverrides[strategyId] = true;
        _logger.LogInformation("Strategy enabled: {Id}", strategyId);
    }

    public void Disable(string strategyId)
    {
        _enabledOverrides[strategyId] = false;
        _logger.LogInformation("Strategy disabled: {Id}", strategyId);
    }

    public void NotifyResult(string strategyId, TradeResult result)
    {
        if (!_stats.TryGetValue(strategyId, out var stats))
        {
            stats = new StrategyPerformanceStats();
            _stats[strategyId] = stats;
        }

        _stats[strategyId] = stats with
        {
            TotalTrades = stats.TotalTrades + 1,
            WinningTrades = result.NetPnL.Amount > 0 ? stats.WinningTrades + 1 : stats.WinningTrades,
            TotalPnL = stats.TotalPnL + result.NetPnL.Amount,
            ProfitFactor = stats.TotalPnL + result.NetPnL.Amount != 0
                ? (stats.TotalPnL + result.NetPnL.Amount) / Math.Max(1, stats.TotalTrades + 1 - stats.WinningTrades)
                : 0,
            WinStreak = result.NetPnL.Amount > 0 ? stats.WinStreak + 1 : 0,
            LossStreak = result.NetPnL.Amount <= 0 ? stats.LossStreak + 1 : 0,
        };

        if (_rotation?.Mode == RotationMode.PerformanceBased
            && _stats[strategyId].TotalTrades >= _rotation.MinTradesForEvaluation
            && _stats[strategyId].WinRate < _rotation.MinWinRateToKeepActive)
        {
            Disable(strategyId);
            _logger.LogWarning("Performance rotation: {Id} disabled (WinRate={Rate:F2})",
                strategyId, _stats[strategyId].WinRate);
        }
    }

    public StrategyBankSnapshot GetSnapshot()
    {
        var strategies = _registry.GetAll().Select(s =>
        {
            var st = _stats.GetValueOrDefault(s.Id, new StrategyPerformanceStats());
            var enabled = _enabledOverrides.TryGetValue(s.Id, out var en) ? en : s.Config.Enabled;
            return new StrategyStatus
            {
                Id = s.Id,
                DisplayName = s.DisplayName,
                IsEnabled = enabled,
                Stats = st,
            };
        }).ToList();

        return new StrategyBankSnapshot { Strategies = strategies };
    }
}
