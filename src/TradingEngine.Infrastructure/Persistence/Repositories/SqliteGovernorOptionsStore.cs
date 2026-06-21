using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteGovernorOptionsStore(TradingDbContext db, Microsoft.Extensions.Logging.ILogger<SqliteGovernorOptionsStore> logger) : IGovernorOptionsStore
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<GovernorOptions> GetAsync(CancellationToken ct)
    {
        var entity = await db.GovernorOptions.FirstOrDefaultAsync(ct);
        if (entity is not null)
        {
            try { return JsonSerializer.Deserialize<GovernorOptions>(entity.Json, _jsonOpts) ?? new(); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to deserialize governor options JSON — falling back to defaults"); }
        }
        return new GovernorOptions();
    }

    public async Task UpsertAsync(GovernorOptions options, CancellationToken ct)
    {
        var entity = await db.GovernorOptions.FirstOrDefaultAsync(ct);
        if (entity is null)
        {
            entity = new GovernorOptionsEntity { Id = "default" };
            db.GovernorOptions.Add(entity);
        }
        entity.Json = JsonSerializer.Serialize(options, _jsonOpts);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
