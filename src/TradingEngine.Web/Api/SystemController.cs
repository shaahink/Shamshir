using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Configuration;
using TradingEngine.Infrastructure.MarketData;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Services.Helpers;

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

    // R0.2: doctor verb — env health check (app reachable, DB migrated, marketdata coverage, cTrader CLI, creds)
    [HttpGet("doctor")]
    public async Task<IActionResult> GetDoctor(CancellationToken ct)
    {
        var issues = new List<string>();

        // DB migrated?
        try
        {
            await _db.Database.CanConnectAsync(ct);
            var pendingMigrations = (await _db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pendingMigrations.Count > 0)
                issues.Add($"DB has {pendingMigrations.Count} pending migrations");
        }
        catch (Exception ex)
        {
            issues.Add($"DB connect failed: {ex.Message}");
        }

        // Market-data coverage
        if (_marketDataStore is not null)
        {
            try
            {
                var inventory = await _marketDataStore.GetInventoryAsync(ct);
                var totalBars = inventory.Sum(i => i.BarCount);
                if (totalBars == 0)
                    issues.Add("marketdata has 0 bars");
            }
            catch (Exception ex)
            {
                issues.Add($"marketdata query failed: {ex.Message}");
            }
        }
        else
        {
            issues.Add("marketdata store not registered");
        }

        // cTrader CLI
        var ctraderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Spotware", "cTrader");
        var found = false;
        if (System.IO.Directory.Exists(ctraderPath))
        {
            foreach (var dir in System.IO.Directory.EnumerateDirectories(ctraderPath))
            {
                var cliPath = System.IO.Path.Combine(dir, "ctrader-cli.exe");
                if (System.IO.File.Exists(cliPath)) { found = true; break; }
            }
        }
        if (!found)
            issues.Add("cTrader CLI not found in LocalAppData");

        // Creds file
        var credsPath = Environment.GetEnvironmentVariable("CTRADER_PWD_FILE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "ctrader.pwd");
        if (!System.IO.File.Exists(credsPath))
            issues.Add($"creds file not found at '{credsPath}'");

        var passed = issues.Count == 0;
        return Ok(new
        {
            verdict = passed ? "PASS" : "FAIL",
            status = passed ? "ok" : "degraded",
            issues,
            dbPath = (_db.Database.GetDbConnection() as SqliteConnection)?.DataSource ?? "unknown",
            port = 5134,
            passed,
        });
    }

    // F21 (R0.1): health endpoint — app reachable, DB migrated, cTrader CLI locatable.
    [HttpGet("health")]
    public IActionResult GetHealth()
    {
        var dataSource = (_db.Database.GetDbConnection() as SqliteConnection)?.DataSource ?? "unknown";
        return Ok(new
        {
            status = "ok",
            dbPath = dataSource,
            version = "iter-alpha-loop",
        });
    }

    // P1.2 (F9): report config drift between config/*.json and the DB. Read-only — the startup sync
    // already propagates non-hand-edited JSON changes; this surfaces hand-edited conflicts and any edits
    // made to the files while the app is running (pending the next restart).
    [HttpGet("config-drift")]
    public async Task<IActionResult> GetConfigDrift(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<ConfigSyncService>();
        var result = await sync.DetectDriftAsync(ct);
        return Ok(new
        {
            inSync = result.InSync,
            baselined = result.Baselined,
            resyncPending = result.Resynced,
            conflicts = result.Conflicts,
            hasDrift = result.Conflicts > 0 || result.Resynced > 0,
            drift = result.Drift.Select(d => new
            {
                d.ConfigType,
                d.Id,
                status = d.Status.ToString(),
                d.FileHash,
                d.DbSeededHash,
                d.UpdatedAtUtc,
                d.SeededAtUtc,
            }),
            entries = result.Entries.Select(d => new
            {
                d.ConfigType,
                d.Id,
                status = d.Status.ToString(),
            }),
        });
    }

    // P6.3: reconcile health — days since last run, nudge to run compare-both weekly.
    [HttpGet("reconcile-health")]
    public async Task<IActionResult> GetReconcileHealth(CancellationToken ct)
    {
        var latestRun = await _db.BacktestRuns
            .Where(r => r.CompletedAtUtc > DateTime.MinValue)
            .OrderByDescending(r => r.CompletedAtUtc)
            .Select(r => new { r.RunId, r.CompletedAtUtc, r.Venue })
            .FirstOrDefaultAsync(ct);

        var latestCompareTape = await _db.BacktestRuns
            .Where(r => r.ParentRunId != null && r.ParentRunId != ""
                && r.Venue == "tape"
                && r.CompletedAtUtc > DateTime.MinValue)
            .Where(r => _db.BacktestRuns.Any(s =>
                s.ParentRunId == r.ParentRunId && s.Venue == "ctrader"))
            .OrderByDescending(r => r.CompletedAtUtc)
            .Select(r => new { r.RunId, r.CompletedAtUtc, r.ParentRunId })
            .FirstOrDefaultAsync(ct);

        var daysSinceLastRun = latestRun is not null
            ? (int?)(DateTime.UtcNow - latestRun.CompletedAtUtc).TotalDays : (int?)null;

        var daysSinceLastCompare = latestCompareTape is not null
            ? (int?)(DateTime.UtcNow - latestCompareTape.CompletedAtUtc).TotalDays : (int?)null;

        return Ok(new
        {
            daysSinceLastRun,
            lastRunId = latestRun?.RunId,
            lastRunVenue = latestRun?.Venue,
            daysSinceLastCompare,
            lastCompareTapeRunId = latestCompareTape?.RunId,
            warning = daysSinceLastCompare is > 7
                ? $"Last compare-both run was {daysSinceLastCompare} days ago. Run POST /api/runs/compare-both for a weekly health check."
                : (daysSinceLastCompare is null
                    ? "No compare-both runs found. Run POST /api/runs/compare-both to create the first reconcile baseline."
                    : null),
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

    // P0.1: one-off, idempotent backfill for TradeResults persisted before InitialStopLoss existed.
    // Only touches rows where InitialStopLoss IS NULL, so it is safe to re-run (e.g. after a new backtest
    // adds fresh rows that already carry the field, or to pick up a run whose journal wasn't queryable
    // on a prior pass). Takes a file-copy backup of the live db first — see docs/iterations/
    // iter-quant-model/PROGRESS.md (P0.1) for why the journal, not EntrySnapshotJson, is the primary source.
    [HttpPost("backfill-initial-stop")]
    public async Task<IActionResult> BackfillInitialStop(CancellationToken ct)
    {
        string? backupPath = null;
        try
        {
            backupPath = BackupDatabaseFile();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backfill: DB backup copy failed, continuing without one");
        }

        var candidates = await _db.Trades
            .Where(t => t.InitialStopLoss == null)
            .ToListAsync(ct);

        var updatedFromJournal = 0;
        var updatedFromSnapshotFallback = 0;
        var skippedNoSource = 0;

        foreach (var group in candidates.GroupBy(t => t.RunId))
        {
            var runId = group.Key;
            var journalStops = string.IsNullOrEmpty(runId)
                ? new Dictionary<Guid, decimal>()
                : InitialStopBackfiller.ParseOrderProposedStops(
                    await _db.JournalEntries
                        .Where(j => j.RunId == runId && j.EventKind == "OrderProposed")
                        .Select(j => j.EventJson)
                        .ToListAsync(ct));

            foreach (var trade in group)
            {
                var resolution = InitialStopBackfiller.Resolve(trade.OrderId, journalStops, trade.EntrySnapshotJson);
                if (resolution.StopLoss is not { } stop)
                {
                    skippedNoSource++;
                    continue;
                }

                trade.InitialStopLoss = stop;
                var direction = Enum.Parse<TradeDirection>(trade.Direction);
                trade.RMultiple = PipCalculator.RMultiple(
                    direction, new Price(trade.EntryPrice), new Price(trade.ExitPrice), new Price(stop));
                trade.ExitDetailJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    reason = trade.ExitReason,
                    exit = trade.ExitPrice,
                    r = trade.RMultiple,
                    finalStopLoss = trade.StopLoss,
                    initialStopLoss = stop,
                });

                if (resolution.Source == InitialStopBackfiller.Source.Journal) updatedFromJournal++;
                else updatedFromSnapshotFallback++;
            }
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            backupPath,
            totalCandidates = candidates.Count,
            updatedFromJournal,
            updatedFromSnapshotFallback,
            skippedNoSource,
        });
    }

    // P4.1 (F12): one-off, idempotent backfill for TradeResults that are missing MaeR/MfeR
    // (historical trades persisted before the M44 migration). Computes R-normalized excursions from
    // existing pip-based excursions + symbol pip size. Safe to re-run — only touches rows where
    // MaeR IS NULL. Takes a file-copy backup of the live db first.
    [HttpPost("backfill-mae-mfe")]
    public async Task<IActionResult> BackfillMaeMfe(CancellationToken ct)
    {
        string? backupPath = null;
        try
        {
            backupPath = BackupDatabaseFile();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backfill: DB backup copy failed, continuing without one");
        }

        ISymbolInfoRegistry? registry = null;
        using (var scope = _scopeFactory.CreateScope())
        {
            registry = scope.ServiceProvider.GetService<ISymbolInfoRegistry>();
        }

        if (registry is null)
            return StatusCode(500, new { error = "ISymbolInfoRegistry not registered — cannot resolve pip sizes for backfill." });

        var candidates = await _db.Trades
            .Where(t => t.MaeR == null)
            .ToListAsync(ct);

        var updated = 0;
        var skippedNoSymbol = 0;
        var skippedZeroStop = 0;

        foreach (var batch in candidates.Chunk(500))
        {
            foreach (var trade in batch)
            {
                Symbol sym;
                try { sym = Symbol.Parse(trade.Symbol); } catch { skippedNoSymbol++; continue; }
                if (!registry.TryGet(sym, out var symInfo))
                {
                    skippedNoSymbol++;
                    continue;
                }

                var stopPrice = trade.InitialStopLoss ?? trade.StopLoss;
                if (stopPrice == 0 || trade.EntryPrice == 0)
                {
                    skippedZeroStop++;
                    continue;
                }

                var (maeR, mfeR) = MaeMfeNormalizer.Normalize(
                    trade.MaxAdverseExcursion,
                    trade.MaxFavorableExcursion,
                    trade.EntryPrice,
                    stopPrice,
                    symInfo);

                trade.MaeR = maeR;
                trade.MfeR = mfeR;
                updated++;
            }

            await _db.SaveChangesAsync(ct);
        }

        return Ok(new
        {
            backupPath,
            totalCandidates = candidates.Count,
            updated,
            skippedNoSymbol,
            skippedZeroStop,
        });
    }

    private string? BackupDatabaseFile()
    {
        var dataSource = (_db.Database.GetDbConnection() as SqliteConnection)?.DataSource;
        if (string.IsNullOrEmpty(dataSource) || !System.IO.File.Exists(dataSource)) return null;

        var backupPath = Path.Combine(
            Path.GetDirectoryName(dataSource) ?? ".",
            $"{Path.GetFileNameWithoutExtension(dataSource)}.bak-{DateTime.UtcNow:yyyyMMdd-HHmmss}{Path.GetExtension(dataSource)}");
        System.IO.File.Copy(dataSource, backupPath);
        _logger.LogWarning("Backfill: DB backed up to {BackupPath}", backupPath);
        return backupPath;
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
