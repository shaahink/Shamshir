namespace TradingEngine.Engine;

public sealed class GovernorMachine : ITradingGovernor
{
    private readonly GovernorOptions _options;
    private GovernorState _state;

    public GovernorMachine(GovernorOptions options)
    {
        _options = options;
        _state = CreateInitial();
    }

    public GovernorState State => _state;

    public GovernorDecision Evaluate(GovernorContext context)
    {
        if (!_options.Enabled)
            return new GovernorDecision(true, 1.0m, GovernorTradingState.Normal, "Disabled");

        var maxDailyLoss = (decimal)context.Rules.MaxDailyLossPercent;
        var dayNetPnLFraction = context.DayNetPnLFraction;
        var dailyDdFraction = maxDailyLoss > 0
            ? Math.Max(0m, -dayNetPnLFraction) / maxDailyLoss
            : 0m;

        _state = EvaluateStatic(_state, dailyDdFraction, dayNetPnLFraction,
            _options.StreakPauseAt, _options.CoolingOffBars,
            _options.LossBandFractions, _options.LossBandMultipliers,
            _options.ProfitLockEnabled, _options.ProfitLockFraction,
            maxDailyLoss, _options.StreakReduceAt, _options.StreakMultiplier);

        return new GovernorDecision(
            _state.State is GovernorTradingState.Normal or GovernorTradingState.Reduced,
            _state.LastSizeMultiplier, _state.State, _state.Reason);
    }

    public GovernorSnapshot GetSnapshot()
    {
        var maxDailyLoss = (decimal)(_options.LossBandFractions.Length > 0 ? _options.LossBandFractions[^1] : 1);
        var dailyDdFraction = maxDailyLoss > 0
            ? Math.Max(0m, -(decimal)_state.DayNetPnLFraction) / maxDailyLoss
            : 0m;
        var distanceToLimit = dailyDdFraction < 1 ? 1 - dailyDdFraction : 0m;

        return new GovernorSnapshot(
            _state.State, _state.LastSizeMultiplier, _state.ConsecutiveLosses,
            _state.DayNetPnLFraction, distanceToLimit, _state.Reason);
    }

    public void OnTradeClosed(TradeResult result)
    {
        _state = ApplyTradeClosed(_state, result.NetPnL.Amount > 0);
    }

    public void OnBar(DateTime barOpenTimeUtc)
    {
        _state = ApplyBar(_state);
    }

    public void OnDailyReset()
    {
        _state = ApplyDailyReset(_state);
    }

    public void OnWeeklyReset()
    {
    }

    // --- Static pure helpers (the kernel's authority) ---

    public static GovernorState CreateInitial() => new(
        GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial");

    public static GovernorState ApplyBar(GovernorState state)
    {
        if (state.CoolingOffBarsRemaining <= 0) return state;
        var remaining = state.CoolingOffBarsRemaining - 1;
        if (remaining > 0) return state with { CoolingOffBarsRemaining = remaining };

        if (state.State == GovernorTradingState.HardStop)
            return state with { CoolingOffBarsRemaining = 0 };

        return state with
        {
            State = GovernorTradingState.Normal,
            CoolingOffBarsRemaining = 0,
            Reason = "Cooling-off complete"
        };
    }

    public static GovernorState ApplyDailyReset(GovernorState state)
    {
        if (state.State is GovernorTradingState.SoftStop or GovernorTradingState.ProfitLocked
            or GovernorTradingState.Reduced or GovernorTradingState.CoolingOff)
        {
            return state with
            {
                State = GovernorTradingState.Normal,
                ProfitLockedToday = false,
                Reason = "Daily reset"
            };
        }
        return state with { ProfitLockedToday = false };
    }

    public static GovernorState ApplyTradeClosed(GovernorState state, bool isWin)
    {
        var losses = isWin ? 0 : state.ConsecutiveLosses + 1;
        return state with { ConsecutiveLosses = losses };
    }

    public static GovernorState EvaluateStatic(
        GovernorState current, decimal dailyDdFraction, decimal dayNetPnLFraction,
        int streakPauseAt, int coolingOffBars, double[] lossBandFractions,
        double[] lossBandMultipliers, bool profitLockEnabled, double profitLockFraction,
        decimal maxDailyLoss, int streakReduceAt, double streakMultiplier)
    {
        if (current.State == GovernorTradingState.HardStop)
            return current with { Reason = "HardStop: protection mode active" };

        if (current.CoolingOffBarsRemaining > 0)
            return current with { Reason = $"CoolingOff: {current.CoolingOffBarsRemaining} bars remaining" };

        if (current.ProfitLockedToday)
            return current with { Reason = current.Reason };

        if (current.State == GovernorTradingState.SoftStop)
            return current with { Reason = current.Reason };

        for (var i = lossBandFractions.Length - 1; i >= 0; i--)
        {
            if (dailyDdFraction >= (decimal)lossBandFractions[i])
            {
                var state = i == lossBandFractions.Length - 1
                    ? GovernorTradingState.SoftStop
                    : GovernorTradingState.Reduced;
                var mult = (decimal)lossBandMultipliers[Math.Min(i, lossBandMultipliers.Length - 1)];
                var reason = i == lossBandFractions.Length - 1
                    ? $"SoftStop: daily DD {dailyDdFraction:P1} >= {lossBandFractions[i]:P0} limit"
                    : $"Reduced: daily DD {dailyDdFraction:P1} >= {lossBandFractions[i]:P0} band";

                if (state != GovernorTradingState.SoftStop)
                    mult = ApplyStreak(mult, current.ConsecutiveLosses, streakReduceAt, streakMultiplier);

                return current with { State = state, LastSizeMultiplier = mult, Reason = reason };
            }
        }

        if (current.ConsecutiveLosses >= streakPauseAt)
        {
            return current with
            {
                State = GovernorTradingState.CoolingOff,
                CoolingOffBarsRemaining = coolingOffBars,
                LastSizeMultiplier = 0,
                Reason = $"CoolingOff: {current.ConsecutiveLosses} consecutive losses >= pause {streakPauseAt}"
            };
        }

        var baseMultiplier = ApplyStreak(1.0m, current.ConsecutiveLosses, streakReduceAt, streakMultiplier);

        if (profitLockEnabled && !current.ProfitLockedToday
            && dayNetPnLFraction >= (decimal)profitLockFraction * maxDailyLoss)
        {
            return current with
            {
                State = GovernorTradingState.ProfitLocked,
                ProfitLockedToday = true,
                LastSizeMultiplier = 0,
                Reason = $"ProfitLocked: daily gain {dayNetPnLFraction:P1} >= {profitLockFraction:P0} threshold"
            };
        }

        if (baseMultiplier < 1.0m)
        {
            return current with
            {
                State = GovernorTradingState.Reduced,
                LastSizeMultiplier = baseMultiplier,
                Reason = $"Reduced: streak multiplier {baseMultiplier} ({current.ConsecutiveLosses} losses)"
            };
        }

        return current with { State = GovernorTradingState.Normal, LastSizeMultiplier = 1.0m, Reason = "Normal" };
    }

    private static decimal ApplyStreak(decimal baseMult, int consecutiveLosses, int streakReduceAt, double streakMultiplier)
    {
        if (consecutiveLosses >= streakReduceAt)
            return baseMult * (decimal)streakMultiplier;
        return baseMult;
    }
}
