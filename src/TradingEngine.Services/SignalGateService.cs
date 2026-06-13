using System.Collections.Concurrent;

namespace TradingEngine.Services;

public sealed class SignalGateService : ISignalGate
{
    private readonly ConcurrentDictionary<string, ReentryOptions> _configs = new();
    private readonly ConcurrentDictionary<string, CooldownState> _cooldowns = new();
    private DateTime _lastBarTimeUtc;

    public void RegisterStrategy(IStrategyConfig config)
    {
        _configs[config.Id] = config.Reentry ?? new ReentryOptions();
    }

    public SignalGateResult Check(string strategyId, string symbol, TradeDirection direction, DateTime barTimeUtc)
    {
        var key = CooldownKey(strategyId, symbol, direction);
        if (_cooldowns.TryGetValue(key, out var state) && state.RemainingBars > 0)
            return new SignalGateResult(false, state.Reason);

        return new SignalGateResult(true, "OK");
    }

    public void OnPositionOpened(string strategyId, string symbol, TradeDirection direction, DateTime barTimeUtc)
    {
        if (!_configs.TryGetValue(strategyId, out var opts))
            opts = new ReentryOptions();

        if (opts.BlockWhileSameDirectionOpen && opts.CooldownBarsAfterEntry > 0)
        {
            var key = CooldownKey(strategyId, symbol, direction);
            _cooldowns[key] = new CooldownState(opts.CooldownBarsAfterEntry, "entry");
        }
    }

    public void OnPositionClosed(string strategyId, string symbol, TradeDirection direction, string reason, DateTime barTimeUtc)
    {
        if (!_configs.TryGetValue(strategyId, out var opts))
            opts = new ReentryOptions();

        var bars = reason switch
        {
            "SL" or "STOPOUT" => opts.CooldownBarsAfterSl,
            "TP" => opts.CooldownBarsAfterTp,
            _ => 0,
        };

        if (bars > 0)
        {
            var key = CooldownKey(strategyId, symbol, direction);
            _cooldowns[key] = new CooldownState(bars, reason == "SL" ? "SL" : "TP");
        }
    }

    public void OnBar(DateTime barTimeUtc)
    {
        if (barTimeUtc <= _lastBarTimeUtc) return;
        _lastBarTimeUtc = barTimeUtc;

        foreach (var key in _cooldowns.Keys.ToList())
        {
            var state = _cooldowns[key];
            var remaining = state.RemainingBars - 1;
            if (remaining <= 0)
                _cooldowns.TryRemove(key, out _);
            else
                _cooldowns[key] = state with { RemainingBars = remaining };
        }
    }

    private static string CooldownKey(string strategyId, string symbol, TradeDirection direction)
        => $"{strategyId}|{symbol}|{direction}";

    private sealed record CooldownState(int RemainingBars, string Reason);
}
