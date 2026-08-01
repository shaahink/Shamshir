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
    public static AccountSnapshot From(EngineState state, DateTime simTimeUtc, string runId) => new(
        simTimeUtc,
        state.Account.Balance,
        state.Account.Equity,
        state.Account.FloatingPnL,
        state.Drawdown.PeakEquity,
        state.Drawdown.DailyStartEquity,
        state.Drawdown.CurrentDailyDrawdown,
        state.Drawdown.CurrentMaxDrawdown,
        state.Positions.Count,
        runId);
}
