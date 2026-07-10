using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain.Interfaces;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteReferenceScaleLookup : IReferenceScaleLookup
{
    private readonly TradingDbContext _db;

    public SqliteReferenceScaleLookup(TradingDbContext db) => _db = db;

    public double? GetMedianAtrPips(Symbol symbol, Timeframe tf)
    {
        var row = _db.ReferenceScales.AsNoTracking()
            .FirstOrDefault(r => r.Symbol == symbol.Value && r.EntryTimeframe == tf.ToString());
        return row is { MedianAtrPips: > 0 } ? (double?)row.MedianAtrPips : null;
    }

    public double? GetMedianBarRangePips(Symbol symbol, Timeframe tf)
    {
        var row = _db.ReferenceScales.AsNoTracking()
            .FirstOrDefault(r => r.Symbol == symbol.Value && r.EntryTimeframe == tf.ToString());
        return row is { MedianBarRangePips: > 0 } ? (double?)row.MedianBarRangePips : null;
    }

    public int? GetSampleBarCount(Symbol symbol, Timeframe tf)
    {
        var row = _db.ReferenceScales.AsNoTracking()
            .FirstOrDefault(r => r.Symbol == symbol.Value && r.EntryTimeframe == tf.ToString());
        return row is { SampleBarCount: > 0 } ? (int?)row.SampleBarCount : null;
    }
}
