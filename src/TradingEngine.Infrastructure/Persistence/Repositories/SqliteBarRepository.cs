namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteBarRepository(TradingDbContext db) : IBarRepository
{
    public async Task BulkInsertAsync(IReadOnlyList<Bar> bars, CancellationToken ct)
        => await BulkInsertAsync("", bars, ct);

    public async Task BulkInsertAsync(string runId, IReadOnlyList<Bar> bars, CancellationToken ct)
    {
        var entities = bars.Select(b => new BarEntity
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            Symbol = b.Symbol.ToString(),
            Timeframe = b.Timeframe.ToString(),
            OpenTimeUtc = b.OpenTimeUtc,
            Open = b.Open,
            High = b.High,
            Low = b.Low,
            Close = b.Close,
            Volume = b.Volume,
        }).ToList();

        db.Bars.AddRange(entities);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Bar>> GetAsync(Symbol symbol, Timeframe tf, DateTime from, DateTime to, CancellationToken ct)
        => await GetAsync("", symbol, tf, from, to, ct);

    public async Task<IReadOnlyList<Bar>> GetAsync(string runId, Symbol symbol, Timeframe tf, DateTime from, DateTime to, CancellationToken ct)
    {
        var query = db.Bars.AsQueryable();
        if (!string.IsNullOrEmpty(runId))
            query = query.Where(b => b.RunId == runId);
        return await query
            .Where(b => b.Symbol == symbol.ToString()
                && b.Timeframe == tf.ToString()
                && b.OpenTimeUtc >= from
                && b.OpenTimeUtc <= to)
            .OrderBy(b => b.OpenTimeUtc)
            .Select(b => new Bar(
                Symbol.Parse(b.Symbol),
                Enum.Parse<Timeframe>(b.Timeframe),
                b.OpenTimeUtc, b.Open, b.High, b.Low, b.Close, b.Volume))
            .ToListAsync<Bar>(ct);
    }
}
