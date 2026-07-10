using TradingEngine.Engine;

namespace TradingEngine.Host;

public sealed class KernelWeekendFlattenEvaluator(IReadOnlyList<IStrategy> strategies)
{
    public IReadOnlyList<(Guid PositionId, string Reason)> Evaluate(Bar bar, EngineState state)
    {
        if (state.Positions.Count == 0) return [];

        var nextBarOpen = bar.OpenTimeUtc + bar.Timeframe.ToTimeSpan();
        var isLastBeforeWeekend = nextBarOpen.DayOfWeek == DayOfWeek.Saturday
            || nextBarOpen.DayOfWeek == DayOfWeek.Sunday;

        if (!isLastBeforeWeekend) return [];

        List<(Guid, string)>? flattens = null;
        foreach (var (id, ps) in state.Positions)
        {
            if (ps.Phase != PositionPhase.Open) continue;

            var cfg = strategies.FirstOrDefault(s => s.Id == ps.StrategyId)?.Config;
            if (cfg?.FlattenBeforeWeekend != true) continue;

            (flattens ??= []).Add((id, "WeekendFlatten"));
        }
        return (IReadOnlyList<(Guid, string)>?)flattens ?? [];
    }
}
