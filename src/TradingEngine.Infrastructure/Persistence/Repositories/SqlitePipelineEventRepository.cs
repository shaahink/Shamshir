using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqlitePipelineEventRepository(TradingDbContext db) : IPipelineEventRepository
{
    public async Task AppendBatchAsync(IReadOnlyList<PipelineEvent> events, CancellationToken ct)
    {
        var entities = events.Select(e => new PipelineEventEntity
        {
            Id = e.Id,
            RunId = e.RunId,
            Seq = e.Seq,
            Stage = e.Stage,
            CorrelationId = e.CorrelationId,
            SimTimeUtc = e.SimTimeUtc,
            WallTimeUtc = e.WallTimeUtc,
            DetailJson = e.DetailJson,
            PhaseBefore = e.PhaseBefore,
            PhaseAfter = e.PhaseAfter,
            GuardResult = e.GuardResult,
            Reason = e.Reason,
            StrategyId = e.StrategyId,
            NormalizedKind = e.NormalizedKind,
        }).ToList();

        db.PipelineEvents.AddRange(entities);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PipelineEvent>> GetByRunIdAsync(string runId, CancellationToken ct)
    {
        return await db.PipelineEvents
            .Where(e => e.RunId == runId)
            .OrderBy(e => e.Seq)
            .Select(e => new PipelineEvent(
                e.Id, e.RunId, e.Seq, e.Stage, e.CorrelationId,
                e.SimTimeUtc, e.WallTimeUtc, e.DetailJson,
                e.PhaseBefore, e.PhaseAfter, e.GuardResult, e.Reason, e.StrategyId,
                e.NormalizedKind))
            .ToListAsync(ct);
    }
}
