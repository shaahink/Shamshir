using System;
using TradingEngine.Engine;

namespace TradingEngine.Host;

/// <summary>
/// Pure mapping from the authoritative kernel <see cref="EngineState"/> to the Monitor's
/// <see cref="AccountSnapshot"/> (iter-36 K4 gap-4). The imperative <c>AccountProcessor</c> built this from
/// the RiskManager's drawdown + the position tracker's count; the kernel owns both now, so the snapshot is
/// read straight off state. Kept pure + separate so the mapping is unit-tested without an IHost.
/// </summary>
public static class KernelEquitySnapshot
{
    public static AccountSnapshot From(
        EngineState state, DateTime simTimeUtc, string runId, decimal dailyLossLimitFraction = 0m)
    {
        // iter-38 W-A7: distance (as a fraction of the account) before the governor's daily-loss HardStop.
        // CurrentDailyDrawdown and MaxDailyLossPercent are the same comparable fraction (see
        // PropFirmRuleValidator). 0 limit ⇒ unknown ⇒ leave the distance at 0 rather than fabricate.
        var distanceToDailyLimit = dailyLossLimitFraction > 0m
            ? Math.Max(0m, dailyLossLimitFraction - state.Drawdown.CurrentDailyDrawdown)
            : 0m;

        return new(
            simTimeUtc,
            state.Account.Balance,
            state.Account.Equity,
            state.Account.FloatingPnL,
            state.Drawdown.PeakEquity,
            state.Drawdown.DailyStartEquity,
            state.Drawdown.CurrentDailyDrawdown,
            state.Drawdown.CurrentMaxDrawdown,
            state.Positions.Count,
            runId,
            // iter-38 W-A7: carry the authoritative governor band + reason so the Monitor isn't blank.
            state.Governor.State.ToString(),
            state.Governor.Reason,
            distanceToDailyLimit);
    }
}
