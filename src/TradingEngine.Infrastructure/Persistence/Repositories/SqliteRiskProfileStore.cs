using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteRiskProfileStore(TradingDbContext db) : IRiskProfileStore
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async Task<IReadOnlyList<RiskProfile>> GetAllAsync(CancellationToken ct)
    {
        var entities = await db.RiskProfiles
            .OrderBy(p => p.DisplayName)
            .ToListAsync(ct);
        return entities.Select(Deserialize).Where(p => p is not null).Cast<RiskProfile>().ToList();
    }

    public async Task<RiskProfile?> GetByIdAsync(string id, CancellationToken ct)
    {
        var entity = await db.RiskProfiles.FindAsync([id], ct);
        return entity is null ? null : Deserialize(entity);
    }

    public async Task UpsertAsync(RiskProfile profile, CancellationToken ct)
    {
        var entity = await db.RiskProfiles.FindAsync([profile.Id], ct);
        if (entity is null)
        {
            entity = new RiskProfileEntity { Id = profile.Id };
            db.RiskProfiles.Add(entity);
        }
        entity.DisplayName = profile.DisplayName;
        entity.Json = JsonSerializer.Serialize(profile, _jsonOpts);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var entity = await db.RiskProfiles.FindAsync([id], ct);
        if (entity is not null)
        {
            db.RiskProfiles.Remove(entity);
            await db.SaveChangesAsync(ct);
        }
    }

    private static RiskProfile? Deserialize(RiskProfileEntity entity)
    {
        try
        {
            return JsonSerializer.Deserialize<RiskProfile>(entity.Json, _jsonOpts);
        }
        catch { return null; }
    }
}
