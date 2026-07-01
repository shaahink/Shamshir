using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Web.Dtos.Bars;

namespace TradingEngine.Web.Services;

public sealed class BarQueryService : IBarQueryService
{
    private readonly TradingDbContext _db;

    public BarQueryService(TradingDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<BarResponse>> GetBarsAsync(
        string symbol, string timeframe, DateTime? from, DateTime? to, CancellationToken ct)
    {
        // Bars are stored upper-cased ("EURUSD" / "H1"); the API is called with mixed case from the UI
        // (e.g. timeframe=h1). Normalise both sides so a case difference never returns an empty chart.
        var sym = (symbol ?? "").ToUpperInvariant();
        var tf = (timeframe ?? "").ToUpperInvariant();

        var query = _db.Bars.AsQueryable();
        if (!string.IsNullOrEmpty(sym))
            query = query.Where(b => b.Symbol.ToUpper() == sym);
        if (!string.IsNullOrEmpty(tf))
            query = query.Where(b => b.Timeframe.ToUpper() == tf);
        if (from.HasValue)
            query = query.Where(b => b.OpenTimeUtc >= from.Value);
        if (to.HasValue)
            query = query.Where(b => b.OpenTimeUtc <= to.Value);

        var rows = await query.OrderBy(b => b.OpenTimeUtc)
            .Select(b => new { b.OpenTimeUtc, b.Open, b.High, b.Low, b.Close })
            .ToListAsync(ct);

        // Catalog bars (RunId="") and a run's own persisted bars can both match — collapse to one bar
        // per timestamp so lightweight-charts (which rejects duplicate/most-recent-wins times) is happy.
        var result = new List<BarResponse>(rows.Count);
        long lastTime = long.MinValue;
        foreach (var b in rows)
        {
            var t = new DateTimeOffset(b.OpenTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds();
            if (t == lastTime) continue;
            lastTime = t;
            result.Add(new BarResponse { Time = t, Open = b.Open, High = b.High, Low = b.Low, Close = b.Close });
        }
        return result;
    }
}
