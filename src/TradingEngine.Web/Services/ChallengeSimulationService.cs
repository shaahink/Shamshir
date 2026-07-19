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
/// V0 (iter-viability): FTMO removed the evaluation time limits (verified 2026-07-16), so the
/// 30-day PassRate is retained purely as a VELOCITY INDEX while the untimed fields carry rule
/// truth — anchored windows run from every daily start to the end of recorded history;
/// Incomplete there means CENSORED (history exhausted), never failed.
/// <c>PBustBeforeTarget</c> = Fails / (Passes + Fails) over resolved anchored windows (null when
/// none resolved); <c>ETimeToTargetDays</c> / <c>MedianTimeToTargetDays</c> are CALENDAR days
/// from window start to target resolution, inclusive, over passing windows.
/// </summary>
public sealed record ChallengeSurvival(
    double PassRate, int Windows, int Passes, int Fails, int Incompletes, string RuleSetId,
    double? PBustBeforeTarget = null, double? ETimeToTargetDays = null,
    double? MedianTimeToTargetDays = null,
    int UntimedPasses = 0, int UntimedBusts = 0, int UntimedCensored = 0);

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

        var (pBust, eTime, medTime, uPasses, uBusts, uCensored) = ComputeUntimedMetrics(days, ruleSet);

        return new ChallengeSurvival(
            passes / (double)results.Count, results.Count, passes, fails, incompletes, ruleSet.Id,
            pBust, eTime, medTime, uPasses, uBusts, uCensored);
    }

    /// <summary>
    /// V0 rule truth: the FTMO evaluation has NO time limit (verified 2026-07-16), so pass/bust
    /// probability must not be computed against an arbitrary 30-day horizon. One anchored window
    /// starts at every daily bucket and runs to the end of recorded history; a window that ends
    /// without target or bust is CENSORED, not failed. Time-to-target is in calendar days,
    /// window start date to resolution date inclusive.
    /// </summary>
    internal static (double? PBust, double? ETimeDays, double? MedianTimeDays, int Passes, int Busts, int Censored)
        ComputeUntimedMetrics(IReadOnlyList<DailyEquityPoint> days, PropFirmRuleSet ruleSet)
    {
        var passes = 0;
        var busts = 0;
        var censored = 0;
        var timeToTargetDays = new List<double>();

        for (var start = 0; start < days.Count; start++)
        {
            var window = days.Skip(start).ToList();
            var result = ChallengeSimulator.SimulateWindow(window, ruleSet);
            switch (result.Verdict)
            {
                case ChallengeVerdict.Pass:
                    passes++;
                    var resolvedDate = window[result.DayResolved!.Value - 1].Date;
                    timeToTargetDays.Add((resolvedDate - window[0].Date).TotalDays + 1);
                    break;
                case ChallengeVerdict.Fail:
                    busts++;
                    break;
                default:
                    censored++;
                    break;
            }
        }

        var resolved = passes + busts;
        var pBust = resolved > 0 ? busts / (double)resolved : (double?)null;
        double? eTime = timeToTargetDays.Count > 0 ? timeToTargetDays.Average() : null;
        double? medTime = null;
        if (timeToTargetDays.Count > 0)
        {
            var sorted = timeToTargetDays.OrderBy(t => t).ToList();
            medTime = sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
        }

        return (pBust, eTime, medTime, passes, busts, censored);
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
        var buckets = new List<(EquitySnapshot First, EquitySnapshot Last)>();
        var bucketStart = 0;
        for (var i = 1; i <= ordered.Count; i++)
        {
            // A new trading day starts when the governor performs a daily reset (DailyStartEquity
            // changes) OR the calendar date rolls over. The reset check alone misses a genuine reset
            // whose new DailyStartEquity happens to numerically equal the old one (a flat multi-day
            // stretch with no open position) — the calendar check alone misses the reset's true
            // boundary. Together they can't silently merge two real trading days.
            var isBoundary = i == ordered.Count
                || ordered[i].DailyStartEquity != ordered[bucketStart].DailyStartEquity
                || ordered[i].TimestampUtc.Date != ordered[i - 1].TimestampUtc.Date;
            if (!isBoundary) continue;

            buckets.Add((ordered[bucketStart], ordered[i - 1]));
            bucketStart = i;
        }

        var points = new List<DailyEquityPoint>(buckets.Count);
        for (var k = 0; k < buckets.Count; k++)
        {
            var (first, last) = buckets[k];
            var tradesClosed = trades.Count(t => t.ClosedAtUtc >= first.TimestampUtc && t.ClosedAtUtc <= last.TimestampUtc);
            // FTMO truth (V0): a trading day is a day with a trade OPENED (a multi-day hold
            // counts only its entry day), and the day's close BALANCE anchors the next day's
            // daily-loss floor. Opened counting tiles forward to the next bucket's start so a
            // trade entered between two snapshot flushes still lands on the day it opened.
            var nextBucketStart = k + 1 < buckets.Count ? buckets[k + 1].First.TimestampUtc : DateTime.MaxValue;
            var tradesOpened = trades.Count(t => t.OpenedAtUtc >= first.TimestampUtc && t.OpenedAtUtc < nextBucketStart);
            points.Add(new DailyEquityPoint(
                first.TimestampUtc.Date, first.DailyStartEquity, last.Equity, tradesClosed,
                tradesOpened, last.Balance));
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
