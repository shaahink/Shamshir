namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteEquityRepository(TradingDbContext db) : IEquityRepository
{
    public async Task SaveAsync(EquitySnapshot snapshot, CancellationToken ct)
    {
        var entity = new EquitySnapshotEntity
        {
            Id = Guid.NewGuid(),
            TimestampUtc = snapshot.TimestampUtc,
            Balance = snapshot.Balance,
            FloatingPnL = snapshot.FloatingPnL,
            Equity = snapshot.Equity,
            PeakEquity = snapshot.PeakEquity,
            DailyStartEquity = snapshot.DailyStartEquity,
            CurrentDailyDrawdown = snapshot.CurrentDailyDrawdown,
            CurrentMaxDrawdown = snapshot.CurrentMaxDrawdown,
            Mode = snapshot.Mode.ToString(),
        };
        db.EquitySnapshots.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<EquitySnapshot>> GetByDateRangeAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        return await db.EquitySnapshots
            .Where(e => e.TimestampUtc >= from && e.TimestampUtc <= to)
            .OrderBy(e => e.TimestampUtc)
            .Select(e => new EquitySnapshot(
                e.TimestampUtc, e.Balance, e.FloatingPnL, e.Equity,
                e.PeakEquity, e.DailyStartEquity, e.CurrentDailyDrawdown,
                e.CurrentMaxDrawdown, Enum.Parse<EngineMode>(e.Mode)))
            .ToListAsync<EquitySnapshot>(ct);
    }

    public async Task<EquitySnapshot?> GetLatestAsync(CancellationToken ct)
    {
        var entity = await db.EquitySnapshots
            .OrderByDescending(e => e.TimestampUtc)
            .FirstOrDefaultAsync(ct);
        if (entity is null) return null;

        return new EquitySnapshot(
            entity.TimestampUtc, entity.Balance, entity.FloatingPnL, entity.Equity,
            entity.PeakEquity, entity.DailyStartEquity, entity.CurrentDailyDrawdown,
            entity.CurrentMaxDrawdown, Enum.Parse<EngineMode>(entity.Mode));
    }
}
