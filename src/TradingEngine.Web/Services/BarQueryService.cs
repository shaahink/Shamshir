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
        var query = _db.Bars.AsQueryable();
        if (!string.IsNullOrEmpty(symbol))
            query = query.Where(b => b.Symbol == symbol);
        if (!string.IsNullOrEmpty(timeframe))
            query = query.Where(b => b.Timeframe == timeframe);
        if (from.HasValue)
            query = query.Where(b => b.OpenTimeUtc >= from.Value);
        if (to.HasValue)
            query = query.Where(b => b.OpenTimeUtc <= to.Value);

        return await query.OrderBy(b => b.OpenTimeUtc)
            .Select(b => new BarResponse
            {
                Time = new DateTimeOffset(b.OpenTimeUtc).ToUnixTimeSeconds(),
                Open = b.Open,
                High = b.High,
                Low = b.Low,
                Close = b.Close,
            })
            .ToListAsync(ct);
    }
}
