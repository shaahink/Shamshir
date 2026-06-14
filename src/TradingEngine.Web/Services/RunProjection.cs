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
        var pipelineRepo = scope.ServiceProvider.GetRequiredService<IPipelineEventRepository>();

        var events = await pipelineRepo.GetByRunIdAsync(runId, ct);

        var timeline = events.Select(e => new DecisionRecordView(
            e.Seq,
            e.SimTimeUtc,
            e.CorrelationId,
            null,
            e.Stage,
            e.GuardResult,
            e.PhaseBefore,
            e.PhaseAfter,
            e.Reason,
            e.DetailJson
        )).ToList();

        IReadOnlyList<AccountSnapshot> equityCurve = [];
        var snapshotStore = scope.ServiceProvider.GetService<IAccountSnapshotStore>();
        if (snapshotStore is not null)
        {
            equityCurve = await snapshotStore.GetByRunIdAsync(runId, ct);
        }

        return new RunProjectionView(timeline, equityCurve);
    }

    public async Task<IReadOnlyList<DecisionRecordView>> GetSignalsAsync(string runId, CancellationToken ct)
    {
        var projection = await GetRunAsync(runId, ct);
        return projection?.Timeline
            .Where(e => e.Event is "SIGNAL" or "OrderSubmitted")
            .ToList() ?? [];
    }

    public async Task<IReadOnlyList<DecisionRecordView>> GetRejectionsAsync(string runId, CancellationToken ct)
    {
        var projection = await GetRunAsync(runId, ct);
        return projection?.Timeline
            .Where(e => e.Event == "OrderRejected")
            .ToList() ?? [];
    }

    public async Task<IReadOnlyList<DecisionRecordView>> GetGovernorTimelineAsync(string runId, CancellationToken ct)
    {
        var projection = await GetRunAsync(runId, ct);
        return projection?.Timeline
            .Where(e => e.Event == "GovernorStateChanged")
            .ToList() ?? [];
    }
}
