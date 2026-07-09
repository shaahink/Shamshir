using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteStrategyConfigStore(TradingDbContext db, IMemoryCache? cache = null) : IStrategyConfigStore
{
    private const string CacheKey = "strategy_configs_all";

    public async Task<IReadOnlyList<StrategyConfigEntry>> GetAllAsync(CancellationToken ct)
    {
        if (cache?.TryGetValue(CacheKey, out IReadOnlyList<StrategyConfigEntry>? cached) == true && cached is not null)
            return cached;

        var entities = await db.StrategyConfigs
            .AsNoTracking()
            .OrderBy(e => e.DisplayName)
            .ToListAsync(ct);

        var results = new List<StrategyConfigEntry>(entities.Count);
        foreach (var e in entities) results.Add(ToEntry(e));
            var list = results.AsReadOnly();
            cache?.Set(CacheKey, list, TimeSpan.FromMinutes(5));
        return list;
    }

    public async Task UpsertAsync(StrategyConfigEntry entry, CancellationToken ct)
    {
        cache?.Remove(CacheKey);
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
            existing.EntryFilterJson = SerializeOptional(entry.EntryFilter);
            existing.Thesis = entry.Thesis;
            existing.ExpectedTradesPerWeek = entry.ExpectedTradesPerWeek;
            existing.ExpectedHoldBars = entry.ExpectedHoldBars;
            existing.Version++;
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        cache?.Remove(CacheKey);
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
            EntryFilterJson = SerializeOptional(entry.EntryFilter),
            Thesis = entry.Thesis,
            ExpectedTradesPerWeek = entry.ExpectedTradesPerWeek,
            ExpectedHoldBars = entry.ExpectedHoldBars,
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
            EntryFilter = DeserializeOptional<EntryFilterOptions>(entity.EntryFilterJson),
            Thesis = entity.Thesis,
            ExpectedTradesPerWeek = entity.ExpectedTradesPerWeek,
            ExpectedHoldBars = entity.ExpectedHoldBars,
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
