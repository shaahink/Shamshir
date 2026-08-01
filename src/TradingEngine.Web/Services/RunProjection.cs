namespace TradingEngine.Web.Services;

public sealed record DecisionRecordView(
    long Seq,
    DateTime SimTimeUtc,
    string? Symbol,
    string? StrategyId,
    string Event,
    string? GuardResult,
    string? PhaseBefore,
    string? PhaseAfter,
    string? Reason,
    string DetailJson);

public sealed record RunProjectionView(
    IReadOnlyList<DecisionRecordView> Timeline,
    IReadOnlyList<AccountSnapshot> EquityCurve);

public sealed class RunProjection
{
    private readonly IServiceScopeFactory _scopeFactory;

    public RunProjection(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<RunProjectionView?> GetRunAsync(string runId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        // iter-37 K-GAP-4: the timeline is the single StepRecord journal (the old PipelineEvents table is
        // no longer written after the iter-36 cutover). Each StepRecord → one DecisionRecordView row.
        var journals = scope.ServiceProvider.GetRequiredService<IJournalQueryRepository>();
        var timeline = new List<DecisionRecordView>();
        await foreach (var r in journals.StreamByRunAsync(runId, null, ct))
        {
            timeline.Add(new DecisionRecordView(
                r.Seq,
                r.SimTimeUtc,
                Symbol: null,
                StrategyId: r.StrategyVerdicts.Count > 0 ? r.StrategyVerdicts[0].StrategyId : null,
                Event: r.EventKind,
                GuardResult: r.DecisionReason,
                PhaseBefore: null,
                PhaseAfter: null,
                Reason: r.DecisionReason,
                DetailJson: r.EventJson));
        }

        // Equity curve: the persisted EquitySnapshots (flushed at completion, K-GAP-2). Fall back to the
        // in-memory snapshot store for a live / in-progress run (before the completion flush).
        IReadOnlyList<AccountSnapshot> equityCurve = [];
        var equityRepo = scope.ServiceProvider.GetService<IEquityRepository>();
        if (equityRepo is not null)
        {
            var snaps = await equityRepo.GetByRunIdAsync(runId, ct);
            equityCurve = snaps.Select(s => new AccountSnapshot(
                s.TimestampUtc, s.Balance, s.Equity, s.FloatingPnL, s.PeakEquity, s.DailyStartEquity,
                s.CurrentDailyDrawdown, s.CurrentMaxDrawdown, 0, runId)).ToList();
        }
        if (equityCurve.Count == 0)
        {
            var snapshotStore = scope.ServiceProvider.GetService<IAccountSnapshotStore>();
            if (snapshotStore is not null)
                equityCurve = await snapshotStore.GetByRunIdAsync(runId, ct);
        }

        return new RunProjectionView(timeline, equityCurve);
    }

    public async Task<IReadOnlyList<DecisionRecordView>> GetSignalsAsync(string runId, CancellationToken ct)
    {
        var projection = await GetRunAsync(runId, ct);
        return projection?.Timeline
            .Where(e => e.Event is "OrderProposed" or "OrderFilled")
            .ToList() ?? [];
    }

    public async Task<IReadOnlyList<DecisionRecordView>> GetRejectionsAsync(string runId, CancellationToken ct)
    {
        var projection = await GetRunAsync(runId, ct);
        // A rejected proposal stays an OrderProposed StepRecord whose DecisionReason carries the gate's
        // named violation; venue refusals are OrderRejected.
        return projection?.Timeline
            .Where(e => e.Event == "OrderRejected" || (e.Event == "OrderProposed" && e.Reason is not null))
            .ToList() ?? [];
    }

    public async Task<IReadOnlyList<DecisionRecordView>> GetGovernorTimelineAsync(string runId, CancellationToken ct)
    {
        var projection = await GetRunAsync(runId, ct);
        return projection?.Timeline
            .Where(e => e.Reason is not null && e.Reason.StartsWith("GOVERNOR", StringComparison.Ordinal))
            .ToList() ?? [];
    }
}
