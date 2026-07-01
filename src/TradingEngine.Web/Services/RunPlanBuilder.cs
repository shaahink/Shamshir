using TradingEngine.Domain;
using TradingEngine.Web.Dtos.Runs;

namespace TradingEngine.Web.Services;

/// <summary>
/// iter-strategy-system P1 (D3): pure translation of the row-based New-Backtest builder into a
/// <see cref="RunPlan"/> and its execution passes. A row is a (strategy × symbol × timeframe × pack);
/// risk/governor/money stay run-level (handled by the orchestrator/controller, not here). Kept pure and
/// side-effect-free so the routing is deterministically unit-testable.
/// </summary>
public static class RunPlanBuilder
{
    /// <summary>
    /// Build the run plan from explicit builder rows. Disabled or blank rows are dropped; symbol and
    /// timeframe are upper-cased and trimmed; an empty/whitespace pack becomes null (strategy default);
    /// exact duplicates (same strategy+symbol+timeframe+pack) collapse to one entry.
    /// </summary>
    public static RunPlan FromRows(IEnumerable<RunRowRequest> rows)
    {
        var entries = rows
            .Where(r => r.Enabled
                && !string.IsNullOrWhiteSpace(r.StrategyId)
                && !string.IsNullOrWhiteSpace(r.Symbol)
                && !string.IsNullOrWhiteSpace(r.Timeframe))
            .Select(r => new RunPlanEntry(
                r.StrategyId.Trim(),
                r.Symbol.Trim().ToUpperInvariant(),
                r.Timeframe.Trim().ToUpperInvariant(),
                string.IsNullOrWhiteSpace(r.PackId) ? null : r.PackId!.Trim()))
            .Distinct()
            .ToList();

        return new RunPlan(entries);
    }

    /// <summary>
    /// Group a run plan into one execution pass per unique (symbol, timeframe). Each pass carries the
    /// strategy → pack map for the strategies that run in that pass, so the orchestrator can build a
    /// per-pass config: the SAME strategy can therefore carry DIFFERENT packs on different rows. A
    /// strategy appears at most once per (symbol, timeframe); if a caller supplies a duplicate with a
    /// different pack, the last one wins.
    /// </summary>
    public static IReadOnlyList<RunPass> IntoPasses(RunPlan plan)
    {
        return plan.Entries
            .GroupBy(e => (e.Symbol, e.Timeframe))
            .Select(g => new RunPass(
                g.Key.Symbol,
                g.Key.Timeframe,
                g.GroupBy(e => e.StrategyId)
                 .ToDictionary(s => s.Key, s => s.Last().PackId, StringComparer.Ordinal)))
            .ToList();
    }
}

/// <summary>One execution pass of a run: a single (symbol, timeframe) and the strategies+packs that run in it.</summary>
public sealed record RunPass(string Symbol, string Timeframe, IReadOnlyDictionary<string, string?> StrategyPacks);
