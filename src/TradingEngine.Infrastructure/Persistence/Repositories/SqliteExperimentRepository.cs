using Microsoft.EntityFrameworkCore;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteExperimentRepository(TradingDbContext db) : IExperimentRepository
{
    public async Task<ExperimentEntity> CreateAsync(ExperimentEntity experiment, CancellationToken ct)
    {
        db.Experiments.Add(experiment);
        await db.SaveChangesAsync(ct);
        return experiment;
    }

    public async Task UpdateAsync(ExperimentEntity experiment, CancellationToken ct)
    {
        // The same scoped DbContext already tracks the instance from CreateAsync, so attaching a fresh
        // instance with the same key throws "another instance with the same key value is already being
        // tracked". Copy scalar values onto the tracked (or freshly loaded) entity instead.
        var existing = await db.Experiments.FindAsync([experiment.Id], ct);
        if (existing is null)
            db.Experiments.Add(experiment);
        else
            db.Entry(existing).CurrentValues.SetValues(experiment);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ExperimentEntity?> GetByIdAsync(Guid id, CancellationToken ct)
        => await db.Experiments
            .Include(e => e.Runs)
            .FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<ExperimentEntity>> GetAllAsync(CancellationToken ct)
        => await db.Experiments
            .OrderByDescending(e => e.CreatedUtc)
            .Include(e => e.Runs)
            .ToListAsync(ct);

    public async Task<ExperimentRunEntity> AddRunAsync(ExperimentRunEntity run, CancellationToken ct)
    {
        db.ExperimentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<IReadOnlyList<ExperimentRunEntity>> GetRunsAsync(Guid experimentId, CancellationToken ct)
        => await db.ExperimentRuns
            .Where(r => r.ExperimentId == experimentId)
            .OrderBy(r => r.FoldIndex).ThenBy(r => r.VariantLabel)
            .ToListAsync(ct);
}
