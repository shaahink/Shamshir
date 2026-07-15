using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteGovernorOptionsStore : IGovernorOptionsStore
{
    private readonly TradingDbContext _db;
    private readonly IServiceProvider? _services;

    public SqliteGovernorOptionsStore(TradingDbContext db, IServiceProvider? services = null)
    {
        _db = db;
        _services = services;
    }

    private ILogger<SqliteGovernorOptionsStore> Logger =>
        _services?.GetService<ILogger<SqliteGovernorOptionsStore>>()
            ?? NullLogger<SqliteGovernorOptionsStore>.Instance;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<GovernorOptions> GetAsync(CancellationToken ct)
    {
        var entity = await _db.GovernorOptions.FirstOrDefaultAsync(ct);
        if (entity is not null)
        {
            try { return JsonSerializer.Deserialize<GovernorOptions>(entity.Json, _jsonOpts) ?? new(); }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to deserialize GovernorOptions JSON — falling back to defaults");
            }
        }
        return new GovernorOptions();
    }

    public async Task UpsertAsync(GovernorOptions options, CancellationToken ct)
    {
        var entity = await _db.GovernorOptions.FirstOrDefaultAsync(ct);
        if (entity is null)
        {
            entity = new GovernorOptionsEntity { Id = "default" };
            _db.GovernorOptions.Add(entity);
        }
        entity.Json = JsonSerializer.Serialize(options, _jsonOpts);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
