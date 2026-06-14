using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Risk.Governor;

public sealed class TradingGovernorService : ITradingGovernor
{
    private readonly GovernorOptions _options;
    private readonly DrawdownTracker _drawdownTracker;
    private readonly IDecisionJournal? _decisionJournal;
    private readonly string? _runId;
    private readonly IEventBus? _eventBus;
    private readonly ILogger<TradingGovernorService> _logger;

    private GovernorTradingState _state = GovernorTradingState.Normal;
    private int _consecutiveLosses;
    private int _coolingOffBarsRemaining;
    private decimal _dayNetPnLFraction;
    private string _reason = "Initial";
    private bool _profitLockedToday;
    private decimal _lastSizeMultiplier = 1.0m;
    private DateTime _lastBarTimeUtc;

    public TradingGovernorService(
        GovernorOptions options,
        DrawdownTracker drawdownTracker,
        ILogger<TradingGovernorService> logger)
        : this(options, drawdownTracker, null, null, null, logger)
    {
    }

    public TradingGovernorService(
        GovernorOptions options,
        DrawdownTracker drawdownTracker,
        IDecisionJournal? decisionJournal,
        EngineRunContext? runContext,
        IEventBus? eventBus,
        ILogger<TradingGovernorService> logger)
    {
        _options = options;
        _drawdownTracker = drawdownTracker;
        _decisionJournal = decisionJournal;
        _runId = runContext?.RunId;
        _eventBus = eventBus;
        _logger = logger;
    }

    public GovernorDecision Evaluate(GovernorContext context)
    {
        if (!_options.Enabled)
            return new GovernorDecision(true, 1.0m, GovernorTradingState.Normal, "Disabled");

        _dayNetPnLFraction = context.DayNetPnLFraction;

        var maxDailyLoss = (decimal)context.Rules.MaxDailyLossPercent;
        var dailyDdFraction = maxDailyLoss > 0
            ? Math.Max(0m, -_dayNetPnLFraction) / maxDailyLoss
            : 0m;

        var oldState = _state;
        var oldReason = _reason;
        var (candidateState, candidateMultiplier, candidateReason) = DetermineState(dailyDdFraction, context);
        _state = candidateState;
        _reason = candidateReason;
        _lastSizeMultiplier = candidateMultiplier;

        if (candidateState != oldState)
        {
            EmitGovernorDecision(oldState, candidateState, candidateReason);
        }

        return new GovernorDecision(
            candidateState is GovernorTradingState.Normal or GovernorTradingState.Reduced,
            candidateMultiplier,
            candidateState,
            candidateReason);
    }

    private (GovernorTradingState State, decimal Multiplier, string Reason) DetermineState(
        decimal dailyDdFraction, GovernorContext context)
    {
        var maxDailyLoss = (decimal)context.Rules.MaxDailyLossPercent;

        if (_state == GovernorTradingState.HardStop)
            return (GovernorTradingState.HardStop, 0m, "HardStop: protection mode active");

        if (_coolingOffBarsRemaining > 0)
            return (GovernorTradingState.CoolingOff, 0m, $"CoolingOff: {_coolingOffBarsRemaining} bars remaining");

        if (_profitLockedToday)
            return (GovernorTradingState.ProfitLocked, 0m, _reason);

        if (_state == GovernorTradingState.SoftStop)
            return (GovernorTradingState.SoftStop, 0m, _reason);

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

        if (_options.ProfitLockEnabled && !_profitLockedToday
            && context.DayNetPnLFraction >= (decimal)_options.ProfitLockFraction * maxDailyLoss)
        {
            _profitLockedToday = true;
            _reason = $"ProfitLocked: daily gain {context.DayNetPnLFraction:P1} >= {_options.ProfitLockFraction:P0} threshold";
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

    public GovernorSnapshot GetSnapshot()
    {
        var maxDailyLoss = (decimal)(_options.LossBandFractions.Length > 0 ? _options.LossBandFractions[^1] : 1);
        var dailyDdFraction = maxDailyLoss > 0
            ? Math.Max(0m, -_dayNetPnLFraction) / maxDailyLoss
            : 0m;
        var distanceToLimit = dailyDdFraction < 1 ? 1 - dailyDdFraction : 0m;

        return new(
            _state,
            _lastSizeMultiplier,
            _consecutiveLosses,
            _dayNetPnLFraction,
            distanceToLimit,
            _reason);
    }

    public void OnTradeClosed(TradeResult result)
    {
        if (result.NetPnL.Amount > 0)
        {
            _consecutiveLosses = 0;
        }
        else if (result.NetPnL.Amount < 0)
        {
            _consecutiveLosses++;
        }
    }

    public void OnBar(DateTime barOpenTimeUtc)
    {
        // Idempotent per timestamp: multi-symbol/multi-TF calls resolve to fastest timeframe rate.
        if (barOpenTimeUtc <= _lastBarTimeUtc) return;
        _lastBarTimeUtc = barOpenTimeUtc;

        if (_coolingOffBarsRemaining > 0)
        {
            _coolingOffBarsRemaining--;
            if (_coolingOffBarsRemaining == 0)
            {
                _logger.LogInformation("Governor: Cooling-off period ended");
                if (_state != GovernorTradingState.HardStop)
                {
                    var oldState = _state;
                    _state = GovernorTradingState.Normal;
                    _reason = "Cooling-off complete";
                    EmitGovernorDecision(oldState, GovernorTradingState.Normal, _reason);
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
            var oldState = _state;
            _state = GovernorTradingState.Normal;
            var reason = "Daily reset";
            _reason = reason;
            EmitGovernorDecision(oldState, GovernorTradingState.Normal, reason);
        }
    }

    public void OnWeeklyReset()
    {
    }

    private void EmitGovernorDecision(GovernorTradingState from, GovernorTradingState to, string reason)
    {
        if (_decisionJournal is not null && _runId is not null)
        {
            _decisionJournal.Record(new DecisionRecord(
                _runId,
                DateTime.UtcNow,
                0,
                null,
                null,
                from.ToString(),
                "GovernorStateChanged",
                null,
                to.ToString(),
                reason,
                "{}"));
        }

        if (_eventBus is not null)
        {
            _eventBus.PublishAsync(new GovernorStateChanged(from, to, reason, DateTime.UtcNow), CancellationToken.None);
        }
    }
}
