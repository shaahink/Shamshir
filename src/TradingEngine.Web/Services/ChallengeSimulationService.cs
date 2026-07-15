using TradingEngine.Risk.Compliance;

namespace TradingEngine.Web.Services;

public sealed record ChallengeSimulationSummary(
    string RunId,
    string RuleSetId,
    IReadOnlyList<ChallengeWindowResult> Windows,
    double PassRate,
    decimal WorstDailyLossAmount,
    double WorstDailyLossPercent);

/// <summary>
/// R4 — rolling-window challenge simulation. Buckets a completed run's EquitySnapshots into
/// engine-truth trading days (grouped by the DailyStartEquity the governor itself computed,
/// which already respects the ruleset's daily-reset time/timezone — NOT a naive UTC-midnight
/// bucketing) and replays N overlapping 30-day challenge attempts against the REAL sequence via
/// <see cref="ChallengeSimulator"/>.
/// </summary>
public sealed class ChallengeSimulationService
{
    private readonly TradingDbContext _db;
    private readonly IEquityRepository _equityRepo;
    private readonly IRiskProfileStore _riskProfileStore;
    private readonly IPropFirmRuleSetStore _propFirmStore;

    public ChallengeSimulationService(
        TradingDbContext db,
        IEquityRepository equityRepo,
        IRiskProfileStore riskProfileStore,
        IPropFirmRuleSetStore propFirmStore)
    {
        _db = db;
        _equityRepo = equityRepo;
        _riskProfileStore = riskProfileStore;
        _propFirmStore = propFirmStore;
    }

    public async Task<ChallengeSimulationSummary> SimulateAsync(
        string runId, int windowCount, int windowDays, CancellationToken ct)
    {
        var run = await _db.BacktestRuns.FirstOrDefaultAsync(r => r.RunId == runId, ct)
            ?? throw new ArgumentException($"Run {runId} not found.");

        var snapshots = await _equityRepo.GetByRunIdAsync(runId, ct);
        if (snapshots.Count == 0)
            throw new ArgumentException($"Run {runId} has no equity snapshots.");

        var trades = await _db.Trades.Where(t => t.RunId == runId).ToListAsync(ct);
        var days = BuildDailyPoints(snapshots, trades);
        if (days.Count == 0)
            throw new ArgumentException($"Run {runId} produced no daily equity buckets.");

        // A run started without an explicit RiskProfileId override still executed under each
        // strategy's own config-level default ("standard") — that default is never written back
        // onto BacktestRuns.RiskProfileId, so a null here means "standard", not "no risk applied".
        var riskProfileId = string.IsNullOrEmpty(run.RiskProfileId) ? "standard" : run.RiskProfileId;
        var profiles = await _riskProfileStore.GetAllAsync(ct);
        var profile = profiles.FirstOrDefault(p => p.Id == riskProfileId)
            ?? throw new ArgumentException($"Risk profile '{riskProfileId}' not found.");
        if (string.IsNullOrEmpty(profile.PropFirmRuleSetId))
            throw new ArgumentException($"Risk profile '{riskProfileId}' has no PropFirmRuleSetId configured.");

        var ruleSets = await _propFirmStore.GetAllAsync(ct);
        var ruleSet = ruleSets.FirstOrDefault(r => r.Id == profile.PropFirmRuleSetId)
            ?? throw new ArgumentException($"Prop firm rule set '{profile.PropFirmRuleSetId}' not found.");

        var windows = BuildRollingWindows(days, windowCount, windowDays);
        var results = windows.Select(w => ChallengeSimulator.SimulateWindow(w, ruleSet)).ToList();

        return new ChallengeSimulationSummary(
            runId,
            ruleSet.Id,
            results,
            results.Count == 0 ? 0 : results.Count(r => r.Verdict == ChallengeVerdict.Pass) / (double)results.Count,
            results.Count == 0 ? 0 : results.Max(r => r.WorstDailyLossAmount),
            results.Count == 0 ? 0 : results.Max(r => r.WorstDailyLossPercent));
    }

    internal static List<DailyEquityPoint> BuildDailyPoints(
        IReadOnlyList<EquitySnapshot> snapshots, IReadOnlyList<TradeResultEntity> trades)
    {
        var ordered = snapshots.OrderBy(s => s.TimestampUtc).ToList();
        var points = new List<DailyEquityPoint>();
        var bucketStart = 0;
        for (var i = 1; i <= ordered.Count; i++)
        {
            // A new trading day starts when the governor performs a daily reset (DailyStartEquity
            // changes) OR the calendar date rolls over. The reset check alone misses a genuine reset
            // whose new DailyStartEquity happens to numerically equal the old one (a flat multi-day
            // stretch with no open position) — the calendar check alone misses the reset's true
            // ~22:00 UTC boundary. Together they can't silently merge two real trading days.
            var isBoundary = i == ordered.Count
                || ordered[i].DailyStartEquity != ordered[bucketStart].DailyStartEquity
                || ordered[i].TimestampUtc.Date != ordered[i - 1].TimestampUtc.Date;
            if (!isBoundary) continue;

            var first = ordered[bucketStart];
            var last = ordered[i - 1];
            var tradesClosed = trades.Count(t => t.ClosedAtUtc >= first.TimestampUtc && t.ClosedAtUtc <= last.TimestampUtc);
            points.Add(new DailyEquityPoint(first.TimestampUtc.Date, first.DailyStartEquity, last.Equity, tradesClosed));
            bucketStart = i;
        }
        return points;
    }

    internal static List<IReadOnlyList<DailyEquityPoint>> BuildRollingWindows(
        IReadOnlyList<DailyEquityPoint> days, int windowCount, int windowDays)
    {
        var effectiveWindowDays = Math.Min(windowDays, days.Count);
        var maxStart = days.Count - effectiveWindowDays;
        var windows = new List<IReadOnlyList<DailyEquityPoint>>();
        for (var w = 0; w < windowCount; w++)
        {
            var start = windowCount <= 1
                ? 0
                : (int)Math.Round(maxStart * (w / (double)(windowCount - 1)));
            windows.Add(days.Skip(start).Take(effectiveWindowDays).ToList());
        }
        return windows;
    }
}
