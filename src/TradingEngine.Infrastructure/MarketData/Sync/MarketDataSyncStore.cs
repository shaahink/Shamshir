using Microsoft.EntityFrameworkCore;

namespace TradingEngine.Infrastructure.MarketData.Sync;

/// <summary>
/// X4 — persistence for the auto-sync watchlist (<see cref="MarketDataSyncCell"/>). The watchlist is the
/// only durable sync state; the work each tick is derived from live bar coverage, so this store is small
/// and side-effect free beyond CRUD.
/// </summary>
public sealed class MarketDataSyncStore(IDbContextFactory<MarketDataDbContext> factory)
{
    /// <summary>
    /// Idempotently create the watchlist table. On an existing multi-GB <c>marketdata.db</c> EF's
    /// EnsureCreated is a no-op (the bars table already exists), so we add the table with raw
    /// <c>CREATE TABLE IF NOT EXISTS</c>. Safe to call on every startup, fresh or existing.
    /// </summary>
    public static Task EnsureSchemaAsync(MarketDataDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS MarketDataSyncCells (
                Symbol TEXT NOT NULL,
                Timeframe TEXT NOT NULL,
                BackfillFromUtc TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (Symbol, Timeframe)
            );
            """, ct);

    public async Task<IReadOnlyList<MarketDataSyncCell>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.SyncCells.AsNoTracking()
            .OrderBy(c => c.Symbol).ThenBy(c => c.Timeframe).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MarketDataSyncCell>> ListEnabledAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.SyncCells.AsNoTracking().Where(c => c.Enabled).ToListAsync(ct);
    }

    public async Task UpsertAsync(string symbol, string timeframe, DateTime backfillFromUtc,
        bool enabled, CancellationToken ct = default)
    {
        symbol = symbol.ToUpperInvariant();
        timeframe = timeframe.ToLowerInvariant();
        var from = DateTime.SpecifyKind(backfillFromUtc, DateTimeKind.Utc);
        var now = DateTime.UtcNow;

        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.SyncCells.FindAsync([symbol, timeframe], ct);
        if (existing is null)
        {
            db.SyncCells.Add(new MarketDataSyncCell
            {
                Symbol = symbol,
                Timeframe = timeframe,
                BackfillFromUtc = from,
                Enabled = enabled,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }
        else
        {
            existing.BackfillFromUtc = from;
            existing.Enabled = enabled;
            existing.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SetEnabledAsync(string symbol, string timeframe, bool enabled, CancellationToken ct = default)
    {
        symbol = symbol.ToUpperInvariant();
        timeframe = timeframe.ToLowerInvariant();
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.SyncCells.FindAsync([symbol, timeframe], ct);
        if (existing is null) return;
        existing.Enabled = enabled;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string symbol, string timeframe, CancellationToken ct = default)
    {
        symbol = symbol.ToUpperInvariant();
        timeframe = timeframe.ToLowerInvariant();
        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await db.SyncCells.FindAsync([symbol, timeframe], ct);
        if (existing is null) return;
        db.SyncCells.Remove(existing);
        await db.SaveChangesAsync(ct);
    }
}
