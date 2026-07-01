using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqlitePropFirmRuleSetStore(TradingDbContext db) : IPropFirmRuleSetStore
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async Task<IReadOnlyList<PropFirmRuleSet>> GetAllAsync(CancellationToken ct)
    {
        var entities = await db.PropFirmRuleSets
            .OrderBy(p => p.DisplayName)
            .ToListAsync(ct);
        return entities.Select(Deserialize).Where(p => p is not null).Cast<PropFirmRuleSet>().ToList();
    }

    public async Task<PropFirmRuleSet?> GetByIdAsync(string id, CancellationToken ct)
    {
        var entity = await db.PropFirmRuleSets.FindAsync([id], ct);
        return entity is null ? null : Deserialize(entity);
    }

    public async Task UpsertAsync(PropFirmRuleSet ruleSet, CancellationToken ct)
    {
        var entity = await db.PropFirmRuleSets.FindAsync([ruleSet.Id], ct);
        if (entity is null)
        {
            entity = new PropFirmRuleSetEntity { Id = ruleSet.Id };
            db.PropFirmRuleSets.Add(entity);
        }
        entity.DisplayName = ruleSet.DisplayName;
        entity.Json = JsonSerializer.Serialize(ruleSet, _jsonOpts);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var entity = await db.PropFirmRuleSets.FindAsync([id], ct);
        if (entity is not null)
        {
            db.PropFirmRuleSets.Remove(entity);
            await db.SaveChangesAsync(ct);
        }
    }

    private static PropFirmRuleSet? Deserialize(PropFirmRuleSetEntity entity)
    {
        try
        {
            return JsonSerializer.Deserialize<PropFirmRuleSet>(entity.Json, _jsonOpts);
        }
        catch { return null; }
    }
}
