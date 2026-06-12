using Microsoft.Extensions.Logging;

namespace TradingEngine.Risk.Governor;

public sealed class TradingGovernorService : ITradingGovernor
{
    private readonly GovernorOptions _options;
    private readonly DrawdownTracker _drawdownTracker;
    private readonly ILogger<TradingGovernorService> _logger;

    private GovernorTradingState _state = GovernorTradingState.Normal;
    private int _consecutiveLosses;
    private int _coolingOffBarsRemaining;
    private decimal _dayRealizedPnLPercent;
    private string _reason = "Initial";
    private bool _profitLockedToday;

    public TradingGovernorService(
        GovernorOptions options,
        DrawdownTracker drawdownTracker,
        ILogger<TradingGovernorService> logger)
    {
        _options = options;
        _drawdownTracker = drawdownTracker;
        _logger = logger;
    }

    public GovernorDecision Evaluate(GovernorContext context)
    {
        if (!_options.Enabled)
            return new GovernorDecision(true, 1.0m, GovernorTradingState.Normal, "Disabled");

        _dayRealizedPnLPercent = context.DayRealizedPnLPercent;

        var maxDailyLoss = (decimal)context.Rules.MaxDailyLossPercent;
        var dailyDdFraction = maxDailyLoss > 0
            ? context.DayRealizedPnLPercent / maxDailyLoss
            : 0m;

        var (candidateState, candidateMultiplier, candidateReason) = DetermineState(dailyDdFraction);
        return new GovernorDecision(
            candidateState is GovernorTradingState.Normal or GovernorTradingState.Reduced,
            candidateMultiplier,
            candidateState,
            candidateReason);
    }

    private (GovernorTradingState State, decimal Multiplier, string Reason) DetermineState(decimal dailyDdFraction)
    {
        if (_state == GovernorTradingState.HardStop)
            return (GovernorTradingState.HardStop, 0m, "HardStop: protection mode active");

        if (_coolingOffBarsRemaining > 0)
            return (GovernorTradingState.CoolingOff, 0m, $"CoolingOff: {_coolingOffBarsRemaining} bars remaining");

        if (_state == GovernorTradingState.SoftStop || _profitLockedToday)
            return (_state, 0m, _reason);

        var bands = _options.LossBandFractions;
        var multipliers = _options.LossBandMultipliers;

        for (var i = bands.Length - 1; i >= 0; i--)
        {
            if (dailyDdFraction >= (decimal)bands[i])
            {
                var state = i == bands.Length - 1
                    ? GovernorTradingState.SoftStop
                    : GovernorTradingState.Reduced;
                var mult = (decimal)multipliers[Math.Min(i, multipliers.Length - 1)];
                var reason = i == bands.Length - 1
                    ? $"SoftStop: daily DD {dailyDdFraction:P1} >= {bands[i]:P0} limit"
                    : $"Reduced: daily DD {dailyDdFraction:P1} >= {bands[i]:P0} band";

                if (state != GovernorTradingState.SoftStop)
                {
                    mult = ApplyStreakMultiplier(mult);
                }
                return (state, mult, reason);
            }
        }

        if (_consecutiveLosses >= _options.StreakPauseAt)
        {
            _coolingOffBarsRemaining = _options.CoolingOffBars;
            _reason = $"CoolingOff: {_consecutiveLosses} consecutive losses >= pause threshold {_options.StreakPauseAt}";
            return (GovernorTradingState.CoolingOff, 0m, _reason);
        }

        var baseMultiplier = ApplyStreakMultiplier(1.0m);

        if (_options.ProfitLockEnabled && !_profitLockedToday && dailyDdFraction <= -(decimal)_options.ProfitLockFraction)
        {
            _profitLockedToday = true;
            _reason = $"ProfitLocked: daily gain {(-dailyDdFraction):P1} >= {_options.ProfitLockFraction:P0} threshold";
            return (GovernorTradingState.ProfitLocked, 0m, _reason);
        }

        if (baseMultiplier < 1.0m)
        {
            return (GovernorTradingState.Reduced, baseMultiplier,
                $"Reduced: streak multiplier {baseMultiplier} ({_consecutiveLosses} losses)");
        }

        return (GovernorTradingState.Normal, 1.0m, "Normal");
    }

    private decimal ApplyStreakMultiplier(decimal baseMultiplier)
    {
        if (_consecutiveLosses >= _options.StreakReduceAt)
            return baseMultiplier * (decimal)_options.StreakMultiplier;
        return baseMultiplier;
    }

    public GovernorSnapshot GetSnapshot() => new(
        _state,
        ApplyStreakMultiplier(1.0m) < 1.0m ? (decimal)_options.StreakMultiplier : 1.0m,
        _consecutiveLosses,
        _dayRealizedPnLPercent,
        _dayRealizedPnLPercent,
        _reason);

    public void OnTradeClosed(TradeResult result)
    {
        if (result.NetPnL.Amount > 0)
        {
            _consecutiveLosses = 0;
        }
        else
        {
            _consecutiveLosses++;
        }
    }

    public void OnBar(DateTime barOpenTimeUtc)
    {
        if (_coolingOffBarsRemaining > 0)
        {
            _coolingOffBarsRemaining--;
            if (_coolingOffBarsRemaining == 0)
            {
                _logger.LogInformation("Governor: Cooling-off period ended");
                if (_state != GovernorTradingState.HardStop)
                {
                    _state = GovernorTradingState.Normal;
                    _reason = "Cooling-off complete";
                }
            }
        }
    }

    public void OnDailyReset()
    {
        _profitLockedToday = false;
        if (_state is GovernorTradingState.SoftStop or GovernorTradingState.ProfitLocked
            or GovernorTradingState.Reduced or GovernorTradingState.CoolingOff)
        {
            _state = GovernorTradingState.Normal;
            _reason = "Daily reset";
        }
    }

    public void OnWeeklyReset()
    {
    }
}
