namespace TradingEngine.Domain;

public sealed record RunPlan(IReadOnlyList<RunPlanEntry> Entries)
{
    public static readonly RunPlan Empty = new([]);
}

public sealed record RunPlanEntry(string StrategyId, string Symbol, string Timeframe);
