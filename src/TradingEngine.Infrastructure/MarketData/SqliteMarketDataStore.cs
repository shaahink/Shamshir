using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.MarketData;

public sealed class SqliteMarketDataStore(IDbContextFactory<MarketDataDbContext> factory) : IMarketDataStore
{
    private const int ChunkSize = 10_000;

    public async Task<int> WriteBarsAsync(string source, IReadOnlyList<Bar> bars, CancellationToken ct = default,
        IProgress<int>? progress = null)
    {
        if (bars.Count == 0) return 0;
        await using var db = await factory.CreateDbContextAsync(ct);
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        var inserted = 0;
        var processed = 0;
        var now = DateTime.UtcNow;
        foreach (var group in bars.GroupBy(b => (Symbol: b.Symbol.ToString(), Tf: b.Timeframe)))
        {
            var sym = group.Key.Symbol;
            var tf = group.Key.Tf.ToString();
            var ordered = group.OrderBy(b => b.OpenTimeUtc).ToList();
            var min = ordered[0].OpenTimeUtc;
            var max = ordered[^1].OpenTimeUtc;

            var existing = await db.Bars
                .Where(r => r.Symbol == sym && r.Timeframe == tf && r.OpenTimeUtc >= min && r.OpenTimeUtc <= max)
                .Select(r => r.OpenTimeUtc)
                .ToListAsync(ct);
            var seen = existing.ToHashSet();

            var chunk = 0;
            foreach (var b in ordered)
            {
                if (!seen.Add(b.OpenTimeUtc)) continue;
                db.Bars.Add(new MarketDataBarRow
                {
                    Symbol = sym,
                    Timeframe = tf,
                    OpenTimeUtc = b.OpenTimeUtc,
                    Open = (double)b.Open,
                    High = (double)b.High,
                    Low = (double)b.Low,
                    Close = (double)b.Close,
                    Volume = b.Volume,
                    Source = source,
                    Quality = 0,
                    IngestedAtUtc = now,
                });
                inserted++;
                processed++;
                chunk++;

                if (chunk >= ChunkSize)
                {
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                    chunk = 0;
                    progress?.Report(processed);
                }
            }
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
        return inserted;
    }

    public async Task<IReadOnlyList<Bar>> ReadBarsAsync(Symbol symbol, Timeframe tf, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var sym = symbol.ToString();
        var tfs = tf.ToString();
        var rows = await db.Bars.AsNoTracking()
            .Where(r => r.Symbol == sym && r.Timeframe == tfs && r.OpenTimeUtc >= fromUtc && r.OpenTimeUtc <= toUtc)
            .OrderBy(r => r.OpenTimeUtc)
            .ToListAsync(ct);

        return rows.Select(r => new Bar(
            symbol, tf, r.OpenTimeUtc,
            (decimal)r.Open, (decimal)r.High, (decimal)r.Low, (decimal)r.Close, r.Volume)).ToList();
    }

    public async Task<IReadOnlyList<MarketDataInventoryEntry>> GetInventoryAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var raw = await db.Bars.AsNoTracking()
            .GroupBy(r => new { r.Symbol, r.Timeframe, r.Source })
            .Select(g => new
            {
                g.Key.Symbol,
                g.Key.Timeframe,
                g.Key.Source,
                First = g.Min(x => x.OpenTimeUtc),
                Last = g.Max(x => x.OpenTimeUtc),
                Count = (long)g.Count(),
            })
            .ToListAsync(ct);

        return raw
            .Select(x => new MarketDataInventoryEntry(
                x.Symbol, Enum.Parse<Timeframe>(x.Timeframe), x.Source, x.First, x.Last, x.Count))
            .OrderBy(x => x.Symbol).ThenBy(x => x.Timeframe)
            .ToList();
    }

    public async Task<IReadOnlyList<MarketDataGap>> GetGapsAsync(Symbol symbol, Timeframe tf, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var sym = symbol.ToString();
        var tfs = tf.ToString();
        var times = await db.Bars.AsNoTracking()
            .Where(r => r.Symbol == sym && r.Timeframe == tfs && r.OpenTimeUtc >= fromUtc && r.OpenTimeUtc <= toUtc)
            .OrderBy(r => r.OpenTimeUtc)
            .Select(r => r.OpenTimeUtc)
            .ToListAsync(ct);

        var interval = tf.ToTimeSpan();
        var gaps = new List<MarketDataGap>();
        for (var i = 1; i < times.Count; i++)
        {
            var delta = times[i] - times[i - 1];
            if (delta <= interval) continue;
            var missing = (int)(delta.Ticks / interval.Ticks) - 1;
            gaps.Add(new MarketDataGap(times[i - 1], times[i], missing, StraddlesWeekend(times[i - 1], times[i])));
        }
        return gaps;
    }

    public async Task<int> DeleteBarsAsync(Symbol symbol, Timeframe tf, DateTime? fromUtc, DateTime? toUtc, string? source, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var sym = symbol.ToString();
        var tfs = tf.ToString();
        var q = db.Bars.Where(r => r.Symbol == sym && r.Timeframe == tfs);
        if (fromUtc is not null) q = q.Where(r => r.OpenTimeUtc >= fromUtc.Value);
        if (toUtc is not null) q = q.Where(r => r.OpenTimeUtc <= toUtc.Value);
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where(r => r.Source == source);
        return await q.ExecuteDeleteAsync(ct);
    }

    private static bool StraddlesWeekend(DateTime a, DateTime b)
    {
        for (var d = a.Date; d <= b.Date; d = d.AddDays(1))
            if (d.DayOfWeek == DayOfWeek.Saturday) return true;
        return false;
    }
}
