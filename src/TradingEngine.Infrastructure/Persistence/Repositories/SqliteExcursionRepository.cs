namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteExcursionRepository(TradingDbContext db) : IExcursionRepository
{
    public async Task SaveAsync(string runId, Guid positionId, string pathJson, CancellationToken ct)
    {
        db.TradeExcursions.Add(new TradeExcursionEntity
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            PositionId = positionId,
            PathJson = pathJson,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetAsync(string runId, Guid positionId, CancellationToken ct)
    {
        var entity = await db.TradeExcursions
            .Where(e => e.RunId == runId && e.PositionId == positionId)
            .FirstOrDefaultAsync(ct);
        return entity?.PathJson;
    }
}
