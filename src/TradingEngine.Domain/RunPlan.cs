namespace TradingEngine.Domain;

public sealed record RunPlan(IReadOnlyList<RunPlanEntry> Entries)
{
    public static readonly RunPlan Empty = new([]);
}

// iter-strategy-system P1 (D3): a run is an explicit set of rows, each a
// (strategy × symbol × timeframe × add-on pack). PackId is null/empty when the row uses the strategy's
// own default add-ons. The kernel routing only reads StrategyId/Symbol/Timeframe — PackId is consumed at
// config-build time so the SAME strategy can carry DIFFERENT packs on different rows (per-pass resolution).
public sealed record RunPlanEntry(string StrategyId, string Symbol, string Timeframe, string? PackId = null);
