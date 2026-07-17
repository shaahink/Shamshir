using TradingEngine.Domain;
using TradingEngine.Engine;

namespace TradingEngine.Host;

public sealed class KernelDailyDdGuardEvaluator(decimal maxDailyLossFraction, DailyDdBase dailyDdBase)
{
    public IReadOnlyList<(Guid PositionId, string Reason)> Evaluate(Bar bar, EngineState state)
    {
        if (state.Positions.Count == 0) return [];
        if (maxDailyLossFraction <= 0) return [];

        var equity = state.Account.Equity;
        // F79: anchor at day start; DailyDdBase only sizes the allowance (see DrawdownReducer.Apply).
        var dailyAllowanceBase = dailyDdBase == DailyDdBase.DailyStart
            ? state.Drawdown.DailyStartEquity
            : state.Drawdown.InitialAccountBalance;

        if (dailyAllowanceBase <= 0 || state.Drawdown.DailyStartEquity <= 0) return [];

        var floor = state.Drawdown.DailyStartEquity - maxDailyLossFraction * dailyAllowanceBase;
        if (equity > floor) return [];

        List<(Guid, string)> flattens = [];
        foreach (var (id, ps) in state.Positions)
        {
            if (ps.Phase != PositionPhase.Open) continue;
            flattens.Add((id, "DailyDD"));
        }
        return flattens;
    }
}
