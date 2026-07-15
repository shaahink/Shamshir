namespace TradingEngine.Risk.Compliance;

/// <summary>
/// One calendar day of an equity curve, bucketed from EquitySnapshots + closed Trades.
/// <c>StartEquity</c> is the equity at the first snapshot of the day (the day's opening mark);
/// <c>EndEquity</c> is the equity at the day's last snapshot.
/// </summary>
public sealed record DailyEquityPoint(DateTime Date, decimal StartEquity, decimal EndEquity, int TradesClosed);

public enum ChallengeVerdict { Pass, Fail, Incomplete }

public sealed record ChallengeWindowResult(
    DateTime WindowStart,
    DateTime WindowEnd,
    ChallengeVerdict Verdict,
    int? DayResolved,
    int TradingDaysUsed,
    decimal WorstDailyLossAmount,
    double WorstDailyLossPercent,
    double FinalReturnPercent,
    string Reason);

/// <summary>
/// Simulates a single fresh FTMO-style challenge attempt starting from the first day of a
/// <see cref="DailyEquityPoint"/> window, walking the account's REAL (not resampled) historical
/// daily equity path day-by-day — unlike <see cref="PassProbabilityEstimator"/>, which projects
/// FORWARD via Monte Carlo resampling of a PnL distribution. This answers a different question:
/// "if a challenge had actually started on this day, using what really happened next, would it
/// have passed?" — the rolling-window backtest of R4, not a forward risk projection.
/// </summary>
public static class ChallengeSimulator
{
    public static ChallengeWindowResult SimulateWindow(IReadOnlyList<DailyEquityPoint> days, PropFirmRuleSet ruleSet)
    {
        if (days.Count == 0) throw new ArgumentException("Window must contain at least one day.", nameof(days));

        var windowStartEquity = days[0].StartEquity;
        var targetEquity = windowStartEquity * (1m + (decimal)ruleSet.ProfitTargetPercent);
        var maxLossFloor = windowStartEquity * (1m - (decimal)ruleSet.MaxTotalLossPercent);
        var dailyLossLimit = windowStartEquity * (decimal)ruleSet.MaxDailyLossPercent;
        var isBalanceBased = ruleSet.DailyDdBase == DailyDdBase.InitialBalance;

        var tradingDays = 0;
        var worstDailyLossAmount = 0m;
        var worstDailyLossPercent = 0.0;
        var targetReached = false;

        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            if (day.TradesClosed > 0) tradingDays++;

            var dailyLossAmount = day.StartEquity - day.EndEquity;
            if (dailyLossAmount > worstDailyLossAmount)
            {
                worstDailyLossAmount = dailyLossAmount;
                worstDailyLossPercent = (double)(dailyLossAmount / windowStartEquity);
            }

            if (day.EndEquity <= maxLossFloor)
            {
                return Resolve(ChallengeVerdict.Fail, i, "max-loss-breach");
            }

            var dailyBreach = isBalanceBased
                ? dailyLossAmount >= dailyLossLimit
                : day.StartEquity > 0 && dailyLossAmount / day.StartEquity >= (decimal)ruleSet.MaxDailyLossPercent;
            if (dailyBreach)
            {
                return Resolve(ChallengeVerdict.Fail, i, "daily-loss-breach");
            }

            if (day.EndEquity >= targetEquity) targetReached = true;

            if (targetReached && tradingDays >= ruleSet.MinTradingDays)
            {
                return Resolve(ChallengeVerdict.Pass, i, "target-reached");
            }
        }

        return Resolve(ChallengeVerdict.Incomplete, days.Count - 1, "window-elapsed-no-resolution");

        ChallengeWindowResult Resolve(ChallengeVerdict verdict, int dayIndex, string reason)
        {
            var resolvedDay = days[dayIndex];
            var finalReturn = (double)((resolvedDay.EndEquity - windowStartEquity) / windowStartEquity);
            return new ChallengeWindowResult(
                days[0].Date, days[^1].Date, verdict,
                verdict == ChallengeVerdict.Incomplete ? null : dayIndex + 1,
                tradingDays, worstDailyLossAmount, worstDailyLossPercent, finalReturn, reason);
        }
    }
}
