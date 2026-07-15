using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Web.Services;

/// <summary>
/// SetupScore v1 (§2 of iter-alpha-loop/PLAN.md). Deterministic, versioned scorer that reads DB only
/// — never starts a run. Computes a 0-100 composite from expectancy, drawdown, consistency, OOS
/// robustness, and FTMO survival. A failed validity gate persists an ExperimentRun row with a NULL
/// score and the reason (D13) — a census sweep must be able to prove coverage from the DB alone
/// (F5: 248 below-floor cells once left no trace, so the R1 gate could not be evaluated).
/// </summary>
public sealed class SetupScoreService
{
    private readonly TradingDbContext _db;
    private readonly ILogger<SetupScoreService> _logger;
    private static readonly JsonSerializerOptions ScoreJsonOpts = new() { WriteIndented = false };

    // Hard validity floors (D3): a cell needs >= this many trades in its window.
    public const int MinimumTrades = 20;

    public SetupScoreService(TradingDbContext db, ILogger<SetupScoreService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ScoreResult> ScoreRunAsync(
        string backtestRunId, Guid? experimentId, string? variantLabel, int? foldIndex, string? foldRole,
        CancellationToken ct, string? strategyId = null, Guid? walkForwardJobId = null)
    {
        var run = await _db.BacktestRuns.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == backtestRunId, ct);
        if (run is null)
            return ScoreResult.Fail("backtest run not found");

        // Gate booleans up front so a failed gate can persist the full snapshot alongside its reason.
        var tapeVenue = !string.IsNullOrEmpty(run.Venue) && run.Venue.Equals("tape", StringComparison.OrdinalIgnoreCase);
        var noWarnings = string.IsNullOrEmpty(run.WarningsJson) || run.WarningsJson == "[]";
        var isFinished = run.CompletedAtUtc > DateTime.MinValue;
        var status = !string.IsNullOrWhiteSpace(run.Status)
            ? run.Status
            : RunStatusResolver.Resolve(isFinished, run.ErrorMessage, run.WarningsJson);
        var completed = status.Equals(RunStatusResolver.Completed, StringComparison.OrdinalIgnoreCase)
            || status.Equals(RunStatusResolver.CompletedWithWarnings, StringComparison.OrdinalIgnoreCase);

        ValidityGates Gates(bool minTrades) => new()
        {
            MinTrades = minTrades,
            NoWarnings = noWarnings,
            TapeVenue = tapeVenue,
            Completed = completed,
        };

        if (!tapeVenue)
        {
            return await PersistNullScoreAsync(backtestRunId,
                $"venue '{run.Venue ?? "null"}' is not tape — scored search requires tape venue (D1)",
                Gates(false), 0, experimentId, variantLabel, strategyId, foldIndex, foldRole, ct);
        }

        if (!noWarnings)
        {
            return await PersistNullScoreAsync(backtestRunId,
                "run has warnings; only clean tape runs are scorable",
                Gates(false), 0, experimentId, variantLabel, strategyId, foldIndex, foldRole, ct);
        }

        if (!completed)
        {
            return await PersistNullScoreAsync(backtestRunId,
                $"run status '{status}' is not completed",
                Gates(false), 0, experimentId, variantLabel, strategyId, foldIndex, foldRole, ct);
        }

        var tradesQuery = _db.Trades.AsNoTracking()
            .Where(t => t.RunId == backtestRunId);
        if (!string.IsNullOrEmpty(strategyId))
            tradesQuery = tradesQuery.Where(t => t.StrategyId == strategyId);

        var trades = await tradesQuery.ToListAsync(ct);

        if (trades.Count < MinimumTrades)
        {
            return await PersistNullScoreAsync(backtestRunId,
                $"trades={trades.Count} below floor {MinimumTrades} (D3)",
                Gates(false), trades.Count, experimentId, variantLabel, strategyId, foldIndex, foldRole, ct);
        }

        var equitySnaps = await _db.EquitySnapshots.AsNoTracking()
            .Where(e => e.RunId == backtestRunId)
            .OrderBy(e => e.TimestampUtc)
            .ToListAsync(ct);

        // Components
        var expectancy = ComputeExpectancy(trades);
        // F60: BacktestRuns.MaxDrawdownPct is stored as a FRACTION (0.014 = 1.40%), but
        // ComputeDrawdownScore's thresholds (<=3 -> 100, >=10 -> 0) are percent-scaled.
        var drawdownPctValue = run.MaxDrawdownPct * 100;
        var drawdownScore = ComputeDrawdownScore(drawdownPctValue);
        var consistency = ComputeConsistency(trades);
        var ftmoSurvival = ComputeFtmoSurvival(equitySnaps, run.BacktestFrom, run.BacktestTo);
        // F62: OOS robustness — Walk-Forward Efficiency (Pardo), the standard measure in walk-forward
        // methodology: OOS test profit as a fraction of the in-sample train profit that chose the
        // params. null unless the caller names a completed walk-forward job for this cell.
        var oosRatio = walkForwardJobId.HasValue
            ? await ComputeOosRatioAsync(walkForwardJobId.Value, ct)
            : null;

        var hasOos = oosRatio.HasValue;
        var oosScore = hasOos ? Math.Clamp(oosRatio!.Value * 100, 0, 100) : (double?)null;

        // Weighted composite
        var weights = ScoreWeights.Default;
        double total = 0;
        double totalWeight = 0;

        total += expectancy * weights.Expectancy;
        totalWeight += weights.Expectancy;
        if (ftmoSurvival.HasValue) { total += ftmoSurvival.Value * weights.FtmoSurvival; totalWeight += weights.FtmoSurvival; }
        total += drawdownScore * weights.Drawdown;
        totalWeight += weights.Drawdown;
        total += consistency * weights.Consistency;
        totalWeight += weights.Consistency;
        if (oosScore.HasValue) { total += oosScore.Value * weights.Robustness; totalWeight += weights.Robustness; }

        var composite = totalWeight > 0 ? Math.Round(total / totalWeight, 1) : 0;

        var scoreJson = JsonSerializer.Serialize(new SetupScore
        {
            Version = "sv1",
            VersionKind = hasOos ? "sv1" : "sv1-partial",
            Composite = composite,
            Components = new ScoreComponents
            {
                ExpectancyR = trades.Average(t => t.RMultiple),
                Expectancy = expectancy,
                FtmoSurvival = ftmoSurvival,
                Drawdown = drawdownScore,
                DrawdownPct = (double)drawdownPctValue,
                Consistency = consistency,
                RobustnessOos = oosScore,
                OosRatio = oosRatio,
            },
            ValidityGates = Gates(true),
            Trades = trades.Count,
            TotalNetPnl = (double)trades.Sum(t => t.NetPnLAmount),
            ComputedAtUtc = DateTime.UtcNow,
        }, ScoreJsonOpts);

        await UpsertExperimentRunAsync(backtestRunId, scoreJson, experimentId, variantLabel, strategyId,
            foldIndex, foldRole, ct);

        _logger.LogInformation("SETUP_SCORE|run={RunId}|composite={Composite}|version={Version}|trades={Trades}",
            backtestRunId, composite, hasOos ? "sv1" : "sv1-partial", trades.Count);

        return ScoreResult.Pass(composite, hasOos ? "sv1" : "sv1-partial", scoreJson);
    }

    // D13: a validity-gate failure is still a census result. Persist the cell with a null score and
    // the reason, so `scoreboard` can prove "scored-or-null with reasons" coverage from the DB alone.
    private async Task<ScoreResult> PersistNullScoreAsync(
        string backtestRunId, string reason, ValidityGates gates, int tradeCount,
        Guid? experimentId, string? variantLabel, string? strategyId, int? foldIndex, string? foldRole,
        CancellationToken ct)
    {
        var scoreJson = JsonSerializer.Serialize(new SetupScore
        {
            Version = "sv1",
            VersionKind = "sv1-null",
            Composite = null,
            NullReason = reason,
            ValidityGates = gates,
            Trades = tradeCount,
            ComputedAtUtc = DateTime.UtcNow,
        }, ScoreJsonOpts);

        await UpsertExperimentRunAsync(backtestRunId, scoreJson, experimentId, variantLabel, strategyId,
            foldIndex, foldRole, ct);

        _logger.LogInformation("SETUP_SCORE|run={RunId}|composite=null|reason={Reason}", backtestRunId, reason);
        return ScoreResult.NullScore(reason, scoreJson);
    }

    private async Task UpsertExperimentRunAsync(
        string backtestRunId, string scoreJson, Guid? experimentId, string? variantLabel, string? strategyId,
        int? foldIndex, string? foldRole, CancellationToken ct)
    {
        var effectiveVariant = variantLabel ?? strategyId ?? "";
        var targetExperimentId = experimentId ?? await GetOrCreateDefaultExperimentAsync(ct);

        var existing = await _db.ExperimentRuns
            .FirstOrDefaultAsync(er => er.ExperimentId == targetExperimentId && er.BacktestRunId == backtestRunId
                && er.VariantLabel == effectiveVariant, ct);

        if (existing is not null)
        {
            existing.ScoreJson = scoreJson;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            existing.VariantLabel = variantLabel ?? existing.VariantLabel;
            if (foldIndex.HasValue) existing.FoldIndex = foldIndex.Value;
            if (foldRole is not null) existing.FoldRole = foldRole;
        }
        else
        {
            _db.ExperimentRuns.Add(new ExperimentRunEntity
            {
                Id = Guid.NewGuid(),
                ExperimentId = targetExperimentId,
                BacktestRunId = backtestRunId,
                VariantLabel = effectiveVariant,
                FoldIndex = foldIndex ?? 0,
                FoldRole = foldRole ?? "Train",
                ScoreJson = scoreJson,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<ScoreboardResult> GetScoreboardAsync(Guid experimentId, int top, CancellationToken ct)
    {
        var experiment = await _db.Experiments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == experimentId, ct);
        if (experiment is null)
            return new ScoreboardResult { Error = $"Experiment {experimentId} not found" };

        var runs = await _db.ExperimentRuns.AsNoTracking()
            .Where(er => er.ExperimentId == experimentId)
            .ToListAsync(ct);

        var scored = new List<ScoreboardEntry>();
        var nullRuns = 0;
        foreach (var run in runs)
        {
            if (string.IsNullOrEmpty(run.ScoreJson) || run.ScoreJson == "{}") continue;
            try
            {
                var score = JsonSerializer.Deserialize<SetupScore>(run.ScoreJson);
                if (score is null) continue;
                if (score.Composite is null)
                {
                    // D13 null cell — counts toward census coverage, never toward the ranking.
                    nullRuns++;
                    continue;
                }
                scored.Add(new ScoreboardEntry(run.BacktestRunId, run.VariantLabel, score));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize ScoreJson for run {RunId}", run.BacktestRunId);
            }
        }

        var topN = scored.OrderByDescending(s => s.Score!.Composite).Take(top).ToList();

        return new ScoreboardResult
        {
            ExperimentId = experimentId,
            ExperimentName = experiment.Name,
            TotalRuns = runs.Count,
            ScoredRuns = scored.Count,
            NullRuns = nullRuns,
            Top = topN,
        };
    }

    private async Task<Guid> GetOrCreateDefaultExperimentAsync(CancellationToken ct)
    {
        var existing = await _db.Experiments.FirstOrDefaultAsync(e => e.Name == "default-sv1", ct);
        if (existing is not null) return existing.Id;

        var id = Guid.NewGuid();
        _db.Experiments.Add(new ExperimentEntity
        {
            Id = id,
            Name = "default-sv1",
            Hypothesis = "Default scoring bucket for ad-hoc scored runs",
            SpecJson = "{}",
            Status = "Active",
            CreatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return id;
    }

    // F62: Walk-Forward Efficiency = sum(OOS test profit) / sum(in-sample profit of the chosen param),
    // aggregated across every fold in the job — Pardo's standard walk-forward methodology, generally
    // read as: >=50-70% is a robust setup, <50% (the plan's D-gate) means the edge doesn't survive
    // contact with unseen data. If the folds' chosen params were themselves net-losing in-sample
    // (trainSum <= 0), there is no genuine in-sample edge to claim efficiency against — score 0
    // rather than leaving it null (a silent partial score would hide a real walk-forward failure).
    internal async Task<double?> ComputeOosRatioAsync(Guid walkForwardJobId, CancellationToken ct)
    {
        var windows = await _db.Set<WalkForwardWindowResultEntity>()
            .AsNoTracking()
            .Where(w => w.JobId == walkForwardJobId && w.PlateauValue != null)
            .ToListAsync(ct);

        if (windows.Count == 0) return null;

        var testSum = windows.Sum(w => w.TestNetProfit);
        var trainSum = windows.Sum(w => (decimal)w.PlateauValue!.Value);

        return trainSum > 0 ? (double)(testSum / trainSum) : 0.0;
    }

    internal static double ComputeExpectancy(IReadOnlyList<TradeResultEntity> trades)
    {
        if (trades.Count == 0) return 0;
        var meanR = (double)trades.Average(t => t.RMultiple);
        // ≤0R → 0, ≥0.5R → 100, linear between
        if (meanR <= 0) return 0;
        if (meanR >= 0.5) return 100;
        return (meanR / 0.5) * 100;
    }

    internal static double ComputeDrawdownScore(decimal maxDdPct)
    {
        var dd = (double)maxDdPct;
        // ≤3% → 100, ≥10% → 0, linear
        if (dd <= 3) return 100;
        if (dd >= 10) return 0;
        return 100 - ((dd - 3) / (10 - 3)) * 100;
    }

    internal static double ComputeConsistency(IReadOnlyList<TradeResultEntity> trades)
    {
        if (trades.Count == 0) return 0;
        var months = trades
            .Where(t => t.ClosedAtUtc > DateTime.MinValue)
            .GroupBy(t => new { t.ClosedAtUtc.Year, t.ClosedAtUtc.Month })
            .ToList();
        if (months.Count == 0) return 0;
        var profitable = months.Count(g => g.Sum(t => t.NetPnLAmount) > 0);
        return ((double)profitable / months.Count) * 100;
    }

    internal static double? ComputeFtmoSurvival(
        IReadOnlyList<EquitySnapshotEntity> snaps, DateTime from, DateTime to)
    {
        // Placeholder: requires rolling 30-day challenge simulation with governor rules.
        // Returns null when insufficient snapshots exist (< 30 days of data).
        if (snaps.Count < 24 * 30) return null; // approx 24 H1 bars per day × 30 days
        // Simple approximation: how often does equity never dip below 10% from peak?
        // Full implementation needs governor rules + daily DD tracking. Deferred to R3.
        if (snaps.Count == 0) return null;
        var initialBalance = snaps.First().Equity;
        if (initialBalance <= 0) return null;
        var stages = Math.Max(1, (int)((to - from).TotalDays / 30));
        var failures = 0;
        var stageSize = snaps.Count / stages;
        for (var i = 0; i < stages; i++)
        {
            var start = i * stageSize;
            var end = Math.Min(start + stageSize, snaps.Count);
            var stageEquity = snaps[start].Equity;
            if (stageEquity <= 0) { failures++; continue; }
            var stageMaxDd = 0m;
            for (var j = start; j < end; j++)
            {
                var dd = (stageEquity - snaps[j].Equity) / stageEquity;
                if (dd > stageMaxDd) stageMaxDd = dd;
            }
            if ((double)stageMaxDd > 0.10) failures++;
        }
        return ((double)(stages - failures) / stages) * 100;
    }
}

public sealed record ScoreResult(bool Passed, double Composite, string Version, string ScoreJson, string? Reason)
{
    public static ScoreResult Pass(double composite, string version, string scoreJson) =>
        new(true, composite, version, scoreJson, null);
    public static ScoreResult Fail(string reason) =>
        new(false, 0, "sv1", "{}", reason);
    /// <summary>A validity-gate failure that DID persist a null-score ExperimentRun row (D13).</summary>
    public static ScoreResult NullScore(string reason, string scoreJson) =>
        new(false, 0, "sv1-null", scoreJson, reason);
}

public sealed record ScoreWeights(double Expectancy, double FtmoSurvival, double Drawdown, double Consistency, double Robustness)
{
    public static readonly ScoreWeights Default = new(0.30, 0.25, 0.15, 0.15, 0.15);
}

public sealed record SetupScore
{
    public string Version { get; init; } = "sv1";
    public string VersionKind { get; init; } = "sv1-partial";
    /// <summary>Null when a validity gate failed — <see cref="NullReason"/> says which (D13).</summary>
    public double? Composite { get; init; }
    public string? NullReason { get; init; }
    public ScoreComponents Components { get; init; } = new();
    public ValidityGates ValidityGates { get; init; } = new();
    public int Trades { get; init; }
    public double TotalNetPnl { get; init; }
    public DateTime ComputedAtUtc { get; init; }
}

public sealed record ScoreComponents
{
    public double ExpectancyR { get; init; }
    public double Expectancy { get; init; }
    public double? FtmoSurvival { get; init; }
    public double Drawdown { get; init; }
    public double DrawdownPct { get; init; }
    public double Consistency { get; init; }
    public double? RobustnessOos { get; init; }
    public double? OosRatio { get; init; }
}

public sealed record ValidityGates
{
    public bool MinTrades { get; init; }
    public bool NoWarnings { get; init; }
    public bool TapeVenue { get; init; }
    public bool Completed { get; init; }
}

public sealed record ScoreboardResult
{
    public Guid ExperimentId { get; init; }
    public string ExperimentName { get; init; } = "";
    public int TotalRuns { get; init; }
    public int ScoredRuns { get; init; }
    /// <summary>Cells persisted with a null score + reason (D13) — census coverage, not rankable.</summary>
    public int NullRuns { get; init; }
    public List<ScoreboardEntry> Top { get; init; } = [];
    public string? Error { get; init; }
}

public sealed record ScoreboardEntry(string BacktestRunId, string VariantLabel, SetupScore? Score);
