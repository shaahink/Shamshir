using TradingEngine.Engine;

namespace TradingEngine.Host;

/// <summary>
/// The kernel-path adapter for P2.4/D6 time-flatten. It is the impure half of the seam: it reads each
/// open position's owning strategy's <see cref="IStrategyConfig.FlattenAtUtc"/> (which the pure kernel
/// cannot — strategy config isn't part of <see cref="EngineState"/>) and decides which positions must be
/// force-closed because the bar's time-of-day has reached that strategy's daily flatten time. The kernel
/// loop turns each decision into a <see cref="CloseRequested"/> event, which the reducer already applies
/// purely (an existing, previously-unwired mechanism — see <c>EngineReducer.HandleCloseRequested</c>).
/// </summary>
public sealed class KernelTimeFlattenEvaluator(IReadOnlyList<IStrategy> strategies)
{
    /// <summary>Positions (on this bar's symbol) that must be flattened because their strategy's daily
    /// flatten time has been reached as of this bar. Pure of kernel state.</summary>
    public IReadOnlyList<(Guid PositionId, string Reason)> Evaluate(Bar bar, EngineState state)
    {
        if (state.Positions.Count == 0) return [];

        List<(Guid, string)>? flattens = null;
        foreach (var (id, ps) in state.Positions)
        {
            if (ps.Phase != PositionPhase.Open || ps.Symbol != bar.Symbol) continue;

            var flattenAt = strategies.FirstOrDefault(s => s.Id == ps.StrategyId)?.Config.FlattenAtUtc;
            if (flattenAt is null) continue;

            if (bar.OpenTimeUtc.TimeOfDay >= flattenAt.Value.ToTimeSpan())
                (flattens ??= []).Add((id, "TimeFlatten"));
        }
        return (IReadOnlyList<(Guid, string)>?)flattens ?? [];
    }
}
