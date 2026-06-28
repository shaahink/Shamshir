using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteStrategyConfigStore(TradingDbContext db) : IStrategyConfigStore
{
    public async Task<IReadOnlyList<StrategyConfigEntry>> GetAllAsync(CancellationToken ct)
    {
        var entities = await db.StrategyConfigs
            .OrderBy(e => e.DisplayName)
            .ToListAsync(ct);

        var results = new List<StrategyConfigEntry>(entities.Count);
        foreach (var e in entities)
            results.Add(ToEntry(e));
        return results;
    }

    public async Task UpsertAsync(StrategyConfigEntry entry, CancellationToken ct)
    {
        var existing = await db.StrategyConfigs.FindAsync([entry.Id], ct);
        if (existing is null)
        {
            db.StrategyConfigs.Add(ToEntity(entry));
        }
        else
        {
            existing.DisplayName = entry.DisplayName;
            existing.Enabled = entry.Enabled;
            existing.RiskProfileId = entry.RiskProfileId;
            existing.ParametersJson = RawTextOrEmpty(entry.Parameters);
            existing.PositionManagementJson = SerializeOptional(entry.PositionManagement);
            existing.OrderEntryJson = SerializeOptional(entry.OrderEntry);
            existing.RegimeFilterJson = SerializeOptional(entry.RegimeFilter);
            existing.ReentryJson = SerializeOptional(entry.Reentry);
            existing.Version++;
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        var existing = await db.StrategyConfigs.FindAsync([id], ct);
        if (existing is not null)
        {
            db.StrategyConfigs.Remove(existing);
            await db.SaveChangesAsync(ct);
        }
    }

    private static StrategyConfigEntity ToEntity(StrategyConfigEntry entry)
    {
        return new StrategyConfigEntity
        {
            Id = entry.Id,
            DisplayName = entry.DisplayName,
            Enabled = entry.Enabled,
            RiskProfileId = entry.RiskProfileId,
            ParametersJson = RawTextOrEmpty(entry.Parameters),
            PositionManagementJson = SerializeOptional(entry.PositionManagement),
            OrderEntryJson = SerializeOptional(entry.OrderEntry),
            RegimeFilterJson = SerializeOptional(entry.RegimeFilter),
            ReentryJson = SerializeOptional(entry.Reentry),
            UpdatedAtUtc = DateTime.UtcNow,
        };
    }

    private static StrategyConfigEntry ToEntry(StrategyConfigEntity entity)
    {
        var parameters = JsonSerializer.Deserialize<JsonElement>(entity.ParametersJson);

        return new StrategyConfigEntry(
            entity.Id,
            entity.DisplayName,
            entity.Enabled,
            entity.RiskProfileId,
            parameters)
        {
            RegimeFilter = DeserializeOptional<RegimeFilterOptions>(entity.RegimeFilterJson),
            OrderEntry = DeserializeOptional<OrderEntryOptions>(entity.OrderEntryJson),
            PositionManagement = DeserializeOptional<PositionManagementOptions>(entity.PositionManagementJson),
            Reentry = DeserializeOptional<ReentryOptions>(entity.ReentryJson),
        };
    }

    // A default/Undefined JsonElement (config with no "parameters" block) has no raw text — persist an
    // empty object rather than throwing. Cloned elements from a live document round-trip normally.
    private static string RawTextOrEmpty(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? "{}" : element.GetRawText();

    private static string? SerializeOptional<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.Serialize(value);

    private static T? DeserializeOptional<T>(string? json) where T : class =>
        json is null ? null : JsonSerializer.Deserialize<T>(json);
}
