using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Configuration;
using TradingEngine.Infrastructure.MarketData;
using TradingEngine.Infrastructure.Persistence;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/system")]
public sealed class SystemController : ControllerBase
{
    private readonly TradingDbContext _db;
    private readonly IMarketDataStore? _marketDataStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SystemController> _logger;

    private static readonly string[] RunTables =
    {
        "Trades", "Orders", "Positions", "Events", "EquitySnapshots",
        "JournalEntries", "Bars", "VenueSessions",
        "ExperimentRuns", "Experiments", "Datasets", "ConfigSets", "BacktestRuns"
    };

    public SystemController(
        TradingDbContext db,
        IMarketDataStore? marketDataStore = null,
        IServiceScopeFactory? scopeFactory = null,
        ILogger<SystemController>? logger = null)
    {
        _db = db;
        _marketDataStore = marketDataStore;
        _scopeFactory = scopeFactory!;
        _logger = logger!;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetService<BacktestOrchestrator>();

        return Ok(new
        {
            version = "iter-tape-trust",
            branch = "iter/tape-trust",
            buildDate = DateTime.UtcNow.ToString("O"),
            dataPaths = new
            {
                tradingDb = _db.Database.GetConnectionString(),
            },
            activeRuns = orchestrator?.GetAll().Count ?? 0,
            runningRuns = orchestrator?.GetAll().Count(r => r.Status is "running" or "starting") ?? 0,
            marketDataAvailable = _marketDataStore is not null,
        });
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset([FromBody] ResetRequest req)
    {
        if (req.Confirm is null || req.Confirm != "delete-everything")
            return BadRequest(new { error = "Confirm token must be 'delete-everything'." });

        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetService<BacktestOrchestrator>();
        var hasActive = orchestrator?.GetAll().Any(r => r.Status is "running" or "starting") ?? false;
        if (hasActive)
            return Conflict(new { error = "A backtest is currently running. Cancel or wait for completion before resetting." });

        var targetScope = req.Scope?.ToLowerInvariant() ?? "runs";

        try
        {
            switch (targetScope)
            {
                case "runs":
                    await DeleteRunDataAsync();
                    break;
                case "marketdata":
                    await ClearMarketDataAsync();
                    break;
                case "config":
                    await ReseedConfigAsync(scope);
                    break;
                case "all":
                    await WipeAllAsync(scope);
                    break;
                default:
                    return BadRequest(new { error = "Scope must be 'runs', 'marketdata', 'config', or 'all'." });
            }

            return Ok(new { scope = targetScope, status = "done" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB reset failed: scope={Scope}", targetScope);
            return Problem($"Reset failed: {ex.Message}");
        }
    }

    private async Task DeleteRunDataAsync()
    {
        _logger.LogWarning("Resetting ALL run data...");
        foreach (var table in RunTables)
            await _db.Database.ExecuteSqlAsync(FormattableStringFactory.Create($"DELETE FROM \"{table}\""));

        await _db.Database.ExecuteSqlRawAsync("VACUUM");
    }

    private async Task ReseedConfigAsync(IServiceScope scope)
    {
        _logger.LogWarning("Resetting config tables...");
        await _db.Database.ExecuteSqlAsync(FormattableStringFactory.Create($"DELETE FROM \"StrategyConfigs\""));
        await _db.Database.ExecuteSqlAsync(FormattableStringFactory.Create($"DELETE FROM \"RiskProfiles\""));
        await _db.Database.ExecuteSqlAsync(FormattableStringFactory.Create($"DELETE FROM \"PropFirmRuleSets\""));
        await _db.Database.ExecuteSqlAsync(FormattableStringFactory.Create($"DELETE FROM \"GovernorOptions\""));
        await _db.Database.ExecuteSqlAsync(FormattableStringFactory.Create($"DELETE FROM \"AddOnPacks\""));

        var seeder = scope.ServiceProvider.GetService<StrategyConfigSeeder>();
        if (seeder is not null)
            await seeder.SeedAsync();

        _logger.LogInformation("Config tables reset and re-seeded from JSON");
    }

    private async Task ClearMarketDataAsync()
    {
        if (_marketDataStore is null)
            throw new InvalidOperationException("Market data store not registered.");

        _logger.LogWarning("Clearing ALL market data bars...");
        var inventory = await _marketDataStore.GetInventoryAsync(CancellationToken.None);
        int totalDeleted = 0;
        foreach (var entry in inventory)
        {
            var deleted = await _marketDataStore.DeleteBarsAsync(
                Symbol.Parse(entry.Symbol), entry.Timeframe, null, null, entry.Source, CancellationToken.None);
            totalDeleted += deleted;
        }

        _logger.LogInformation("Market data cleared ({Total} bars deleted across {Count} entries)", totalDeleted, inventory.Count);
    }

    private async Task WipeAllAsync(IServiceScope scope)
    {
        _logger.LogWarning("Wiping ALL trading DB files...");
        SqliteConnection.ClearAllPools();

        var cs = _db.Database.GetConnectionString()!;
        var dbPath = new SqliteConnectionStringBuilder(cs).DataSource;
        var dir = Path.GetDirectoryName(dbPath)!;
        var baseName = Path.GetFileNameWithoutExtension(dbPath);
        var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var source = Path.Combine(dir, $"{baseName}.db{ext}");
            var dest = Path.Combine(dir, $"{baseName}.bak-{ts}.db{ext}");
            if (System.IO.File.Exists(source))
                System.IO.File.Move(source, dest, overwrite: true);
        }

        await _db.Database.MigrateAsync();
        await ReseedConfigAsync(scope);

        _logger.LogInformation("Trading DB wiped. Backup at {Name}.bak-{Ts}.*", baseName, ts);
    }
}

public sealed record ResetRequest
{
    public string? Scope { get; init; }
    public string? Confirm { get; init; }
}
