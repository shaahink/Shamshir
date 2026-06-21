using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

/// <summary>
/// iter-38 (Stream PK1). SQLite-backed <see cref="IAddOnPackStore"/>, mirroring <c>SqliteStrategyConfigStore</c>.
/// The pack payload (breakeven/trailing/partial/ride/dynamic) is stored as a <c>PositionManagementOptions</c>
/// JSON blob so a pack and a strategy share one shape. <c>CreatedAtUtc</c>/<c>UpdatedAtUtc</c> are stamped by
/// <c>AuditStampInterceptor</c> (D5) — no manual timestamping here.
///
/// This is a working skeleton; the agent should add the 3 seeded starter packs (PK1) + DI registration, and
/// (PK3) feed the resolved pack into <c>EffectiveConfigResolver</c> for a run.
/// </summary>
public sealed class SqliteAddOnPackStore(TradingDbContext db) : IAddOnPackStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<AddOnPack>> GetAllAsync(CancellationToken ct)
    {
        var entities = await db.AddOnPacks.OrderBy(e => e.Name).ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    public async Task<AddOnPack?> GetByIdAsync(string id, CancellationToken ct)
    {
        var e = await db.AddOnPacks.FindAsync([id], ct);
        return e is null ? null : ToDomain(e);
    }

    public async Task UpsertAsync(AddOnPack pack, CancellationToken ct)
    {
        var existing = await db.AddOnPacks.FindAsync([pack.Id], ct);
        if (existing is null)
        {
            db.AddOnPacks.Add(new AddOnPackEntity
            {
                Id = pack.Id,
                Name = pack.Name,
                Description = pack.Description,
                AddOnsJson = JsonSerializer.Serialize(pack.AddOns),
                RegimeDetectionEnabled = pack.RegimeDetectionEnabled,
            });
        }
        else
        {
            existing.Name = pack.Name;
            existing.Description = pack.Description;
            existing.AddOnsJson = JsonSerializer.Serialize(pack.AddOns);
            existing.RegimeDetectionEnabled = pack.RegimeDetectionEnabled;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var e = await db.AddOnPacks.FindAsync([id], ct);
        if (e is null) return;
        db.AddOnPacks.Remove(e);
        await db.SaveChangesAsync(ct);
    }

    private static AddOnPack ToDomain(AddOnPackEntity e)
    {
        var addOns = JsonSerializer.Deserialize<PositionManagementOptions>(e.AddOnsJson, Json) ?? new PositionManagementOptions();
        return new AddOnPack(e.Id, e.Name, e.Description, addOns, e.RegimeDetectionEnabled);
    }
}
