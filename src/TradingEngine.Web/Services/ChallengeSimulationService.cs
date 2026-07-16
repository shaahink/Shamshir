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
/// sv2 (F63): challenge survival for scoring. <c>PassRate</c> = Pass / Windows — an Incomplete
/// window is a non-pass on purpose: R4's headline failure mode was velocity (windows that never
/// resolved), and a survival score that ignored incompletes would hide exactly that.
/// </summary>
public sealed record ChallengeSurvival(
    double PassRate, int Windows, int Passes, int Fails, int Incompletes, string RuleSetId);

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

        var (ruleSet, ruleSetError) = await ResolveRuleSetAsync(run, ct);
        if (ruleSet is null)
            throw new ArgumentException(ruleSetError);

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

    /// <summary>
    /// sv2 (F63, iter-structural-edge S0): survival component for <see cref="SetupScoreService"/>.
    /// Rolls a 30-day challenge window from EVERY daily start over the run's real equity path and
    /// returns the pass rate. Null (not 0) when survival cannot be computed at all — no snapshots,
    /// fewer than 30 daily buckets, or no resolvable prop-firm rule set — so the composite skips
    /// the component instead of punishing the cell for missing data.
    /// </summary>
    public async Task<ChallengeSurvival?> ComputeSurvivalAsync(string runId, CancellationToken ct)
    {
        var run = await _db.BacktestRuns.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId, ct);
        if (run is null) return null;

        var snapshots = await _equityRepo.GetByRunIdAsync(runId, ct);
        if (snapshots.Count == 0) return null;

        var trades = await _db.Trades.AsNoTracking().Where(t => t.RunId == runId).ToListAsync(ct);
        var days = BuildDailyPoints(snapshots, trades);
        if (days.Count < SurvivalWindowDays) return null;

        var (ruleSet, _) = await ResolveRuleSetAsync(run, ct);
        if (ruleSet is null) return null;

        // Every possible 30-day start, not a sparse sample: with windowCount = maxStart + 1 the
        // even spread in BuildRollingWindows degenerates to exactly one window per daily start.
        var windowCount = days.Count - SurvivalWindowDays + 1;
        var windows = BuildRollingWindows(days, windowCount, SurvivalWindowDays);
        var results = windows.Select(w => ChallengeSimulator.SimulateWindow(w, ruleSet)).ToList();

        var passes = results.Count(r => r.Verdict == ChallengeVerdict.Pass);
        var fails = results.Count(r => r.Verdict == ChallengeVerdict.Fail);
        var incompletes = results.Count(r => r.Verdict == ChallengeVerdict.Incomplete);
        return new ChallengeSurvival(
            passes / (double)results.Count, results.Count, passes, fails, incompletes, ruleSet.Id);
    }

    internal const int SurvivalWindowDays = 30;

    private async Task<(PropFirmRuleSet? RuleSet, string Error)> ResolveRuleSetAsync(
        BacktestRunEntity run, CancellationToken ct)
    {
        // A run started without an explicit RiskProfileId override still executed under each
        // strategy's own config-level default ("standard") — that default is never written back
        // onto BacktestRuns.RiskProfileId, so a null here means "standard", not "no risk applied".
        var riskProfileId = string.IsNullOrEmpty(run.RiskProfileId) ? "standard" : run.RiskProfileId;
        var profiles = await _riskProfileStore.GetAllAsync(ct);
        var profile = profiles.FirstOrDefault(p => p.Id == riskProfileId);
        if (profile is null)
            return (null, $"Risk profile '{riskProfileId}' not found.");
        if (string.IsNullOrEmpty(profile.PropFirmRuleSetId))
            return (null, $"Risk profile '{riskProfileId}' has no PropFirmRuleSetId configured.");

        var ruleSets = await _propFirmStore.GetAllAsync(ct);
        var ruleSet = ruleSets.FirstOrDefault(r => r.Id == profile.PropFirmRuleSetId);
        return ruleSet is null
            ? (null, $"Prop firm rule set '{profile.PropFirmRuleSetId}' not found.")
            : (ruleSet, "");
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
