using System.Text.Json;
using TradingEngine.CTraderRunner;
using TradingEngine.Domain;
using TradingEngine.Services;

namespace TradingEngine.Web.Services;

/// <summary>Pure parsing of a <see cref="BacktestConfig"/>'s CustomParams into typed run inputs.
/// No I/O — every method here is deterministic on its arguments.</summary>
public static class RunRequestParser
{
    // The New-Backtest strategy picker arrives as a comma-separated "StrategyIds" custom param
    // (empty/absent = run all configured strategies).
    public static string[] ParseStrategyIds(BacktestConfig cfg) =>
        cfg.CustomParams.TryGetValue("StrategyIds", out var ids) && !string.IsNullOrWhiteSpace(ids)
            ? ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

    // iter-strategy-system P1 (D3): the row-based builder serializes its enabled rows (as RunPlanEntry, incl.
    // per-row PackId) into CustomParams["RunRows"]. Absent/blank ⇒ legacy cross-product path.
    public static List<RunPlanEntry> ParseRunPlanEntries(BacktestConfig cfg)
    {
        if (!cfg.CustomParams.TryGetValue("RunRows", out var json) || string.IsNullOrWhiteSpace(json))
            return [];
        try { return JsonSerializer.Deserialize<List<RunPlanEntry>>(json) ?? []; }
        catch (Exception ex)
        {
            // A malformed RunRows must not silently run an empty plan that looks like "all strategies".
            throw new InvalidOperationException("Invalid RunRows payload.", ex);
        }
    }

    public static RunPlan BuildRunPlan(string[] strategyIds, string[] symbols, string[] periods)
    {
        var entries = new List<RunPlanEntry>();
        foreach (var sid in strategyIds)
        {
            foreach (var sym in symbols)
            {
                foreach (var pf in periods)
                {
                    entries.Add(new RunPlanEntry(sid, sym, pf));
                }
            }
        }
        return new RunPlan(entries);
    }

    public static Timeframe ParseTimeframe(string period) => period.ToUpperInvariant() switch
    {
        "M1" => Timeframe.M1,
        "M5" => Timeframe.M5,
        "M15" => Timeframe.M15,
        "M30" => Timeframe.M30,
        "H1" => Timeframe.H1,
        "H4" => Timeframe.H4,
        "D1" => Timeframe.D1,
        _ => Timeframe.H1,
    };

    public static int EstimateBarCount(DateTime start, DateTime end, string period)
    {
        var duration = end - start;
        var minutes = period.ToUpperInvariant() switch
        {
            "M1" => 1.0,
            "M5" => 5.0,
            "M15" => 15.0,
            "M30" => 30.0,
            "H1" => 60.0,
            "H4" => 240.0,
            "D1" => 1440.0,
            _ => 60.0,
        };
        return (int)(duration.TotalMinutes / minutes);
    }

    public static Dictionary<string, StrategyOverride> ParseOverrides(BacktestConfig cfg)
    {
        if (!cfg.CustomParams.TryGetValue("StrategyOverrides", out var json) ||
            string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, StrategyOverride>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
