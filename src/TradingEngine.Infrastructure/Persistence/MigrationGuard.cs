using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Persistence;

/// <summary>
/// Thrown when a process opens a <see cref="TradingDbContext"/> that has un-applied EF migrations.
/// The Host CLI must fail loud (never silently run against a stale schema) — iter-parity-pipeline P1.1,
/// AUDIT F10 (the Host CLI was dead-on-arrival for a month against an un-migrated second DB).
/// </summary>
public sealed class PendingMigrationsException(string dbPath, IReadOnlyList<string> pending)
    : InvalidOperationException(
        $"Database at '{dbPath}' has {pending.Count} pending EF migration(s): "
        + $"{string.Join(", ", pending)}. Run the Web app (which applies migrations on startup) "
        + "against this same database first, or apply migrations with 'dotnet ef database update'.")
{
    public string DbPath { get; } = dbPath;
    public IReadOnlyList<string> Pending { get; } = pending;
}

/// <summary>
/// Guards a DB-touching entry point against an un-migrated schema. Read-only — never applies migrations
/// itself (the Web app owns migration application via <c>MigrateAsync</c> on startup).
/// </summary>
public static class MigrationGuard
{
    /// <summary>The migrations present in the model but not yet applied to the opened database.</summary>
    public static IReadOnlyList<string> GetPending(TradingDbContext db)
        => db.Database.GetPendingMigrations().ToList();

    /// <summary>
    /// Fail loud (throw <see cref="PendingMigrationsException"/>) when the opened database has pending
    /// migrations, logging the exact path it opened. No-op when the schema is current.
    /// </summary>
    public static void EnsureUpToDate(TradingDbContext db, string dbPath, ILogger logger)
    {
        var pending = GetPending(db);
        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is current at {DbPath} (0 pending migrations).", dbPath);
            return;
        }

        logger.LogCritical(
            "Database at {DbPath} has {Count} pending EF migration(s): {Pending}. Refusing to run against "
            + "a stale schema.", dbPath, pending.Count, string.Join(", ", pending));
        throw new PendingMigrationsException(dbPath, pending);
    }
}
