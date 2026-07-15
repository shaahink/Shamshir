using TradingEngine.Risk.Compliance;
using TradingEngine.Services.Helpers;

namespace TradingEngine.Web.Services;

public sealed class PassProbabilityService
{
    private readonly TradingDbContext _db;
    private readonly IPassProbabilityEstimator _estimator;
    private readonly IEquityRepository _equityRepo;
    private readonly IRiskProfileStore _riskProfileStore;
    private readonly IPropFirmRuleSetStore _propFirmStore;

    public PassProbabilityService(
        TradingDbContext db,
        IPassProbabilityEstimator estimator,
        IEquityRepository equityRepo,
        IRiskProfileStore riskProfileStore,
        IPropFirmRuleSetStore propFirmStore)
    {
        _db = db;
        _estimator = estimator;
        _equityRepo = equityRepo;
        _riskProfileStore = riskProfileStore;
        _propFirmStore = propFirmStore;
    }

    public async Task<PassProbabilityEstimate> ComputeAsync(string runId, int daysRemaining, CancellationToken ct)
    {
        var run = await _db.BacktestRuns.FirstOrDefaultAsync(r => r.RunId == runId, ct);
        if (run is null)
            throw new ArgumentException($"Run {runId} not found.");

        var equitySnapshots = await _equityRepo.GetByRunIdAsync(runId, ct);
        var domainSnapshots = equitySnapshots.ToList();

        var dailyPnL = DailyPnLComputer.Compute([], domainSnapshots);
        if (dailyPnL.Count == 0)
        {
            var trades = await _db.Trades.Where(t => t.RunId == runId).OrderBy(t => t.ClosedAtUtc).ToListAsync(ct);
            dailyPnL = trades.GroupBy(t => t.ClosedAtUtc.Date)
                .OrderBy(g => g.Key)
                .Select(g => (decimal)g.Sum(t => t.NetPnLAmount))
                .ToList();
        }

        var currentEquity = domainSnapshots.Count > 0
            ? domainSnapshots[^1].Equity
            : run.InitialBalance + dailyPnL.Sum();

        var initialBalance = domainSnapshots.Count > 0
            ? domainSnapshots[0].Equity
            : run.InitialBalance;

        if (string.IsNullOrEmpty(run.RiskProfileId))
            throw new ArgumentException($"Run {runId} has no RiskProfileId. Cannot compute P(pass).");

        var profiles = await _riskProfileStore.GetAllAsync(ct);
        var profile = profiles.FirstOrDefault(p => p.Id == run.RiskProfileId);
        if (profile is null)
            throw new ArgumentException($"Risk profile '{run.RiskProfileId}' used by run {runId} not found.");
        if (string.IsNullOrEmpty(profile.PropFirmRuleSetId))
            throw new ArgumentException($"Risk profile '{run.RiskProfileId}' has no PropFirmRuleSetId configured.");

        var ruleSets = await _propFirmStore.GetAllAsync(ct);
        var ruleSet = ruleSets.FirstOrDefault(r => r.Id == profile.PropFirmRuleSetId);
        if (ruleSet is null)
            throw new ArgumentException($"Prop firm rule set '{profile.PropFirmRuleSetId}' not found.");

        // P4.5.5: fresh-challenge framing for research surfaces.
        // Start from initial balance, full challenge duration — the run's per-day PnL is
        // a distribution to sample FROM, not a history that gets subtracted from DaysRemaining.
        // The old code clamped DaysRemaining to 1 for any long completed run, giving P(pass) ≈ 0.
        var input = new PassProbabilityInput
        {
            CurrentEquity = initialBalance,
            InitialBalance = initialBalance,
            ProfitTargetPercent = ruleSet.ProfitTargetPercent,
            MaxDailyLossPercent = ruleSet.MaxDailyLossPercent,
            MaxTotalLossPercent = ruleSet.MaxTotalLossPercent,
            DaysRemaining = daysRemaining,
            HistoricalDailyPnL = dailyPnL,
            MonteCarloRuns = 10_000,
            DailyDdBase = ruleSet.DailyDdBase,
        };

        return _estimator.Estimate(input);
    }
}
