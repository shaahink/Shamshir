using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteConfigSetRepository(TradingDbContext db) : IConfigSetRepository
{
    public async Task SaveAsync(ConfigSet config, CancellationToken ct)
    {
        var entity = new ConfigSetEntity
        {
            Id = config.ConfigSetId,
            ContentHash = config.ContentHash,
            Json = config.Json,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.ConfigSets.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ConfigSet?> GetByIdAsync(string configSetId, CancellationToken ct)
    {
        var entity = await db.ConfigSets.FindAsync([configSetId], ct);
        if (entity is null) return null;
        return Map(entity);
    }

    public async Task<IReadOnlyList<ConfigSet>> GetAllAsync(CancellationToken ct)
    {
        return await db.ConfigSets
            .OrderByDescending(e => e.CreatedAtUtc)
            .Select(e => Map(e))
            .ToListAsync(ct);
    }

    public async Task<string?> GetContentHashAsync(string json, CancellationToken ct)
    {
        var hash = ConfigSetHash.Compute(json);
        return await db.ConfigSets
            .Where(e => e.ContentHash == hash)
            .Select(e => (string?)e.ContentHash)
            .FirstOrDefaultAsync(ct);
    }

    private static ConfigSet Map(ConfigSetEntity e) => new(e.Id, e.ContentHash, e.Json);
}
