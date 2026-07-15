using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Configuration;

/// <summary>P3.4 — synchronous lookup for calibrated exit rules from the ExitCalibrations table.</summary>
public sealed class SqliteExitCalibrationLookup(TradingDbContext db) : IExitCalibrationLookup
{
    public ExitCalibrationRecord? Get(string strategyId, string symbol, Timeframe timeframe, string? regime)
    {
        // EF Core can be called sync in .NET — this runs on the hot path at position registration.
        var row = db.ExitCalibrations
            .AsNoTracking()
            .FirstOrDefault(e =>
                e.StrategyId == strategyId &&
                e.Symbol == symbol &&
                e.EntryTimeframe == timeframe.ToString() &&
                e.Regime == regime);

        if (row is null) return null;

        return new ExitCalibrationRecord
        {
            SlAtrMultiple = row.SlAtrMultiple,
            TpRrMultiple = row.TpRrMultiple,
            BeTriggerR = row.BeTriggerR,
            BeOffsetPips = row.BeOffsetPips,
            TrailAtrMultiple = row.TrailAtrMultiple,
            PartialTriggerR = row.PartialTriggerR,
            PartialCloseFraction = row.PartialCloseFraction,
        };
    }
}
