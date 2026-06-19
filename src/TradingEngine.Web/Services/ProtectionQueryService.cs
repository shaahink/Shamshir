using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Web.Dtos.Protection;

namespace TradingEngine.Web.Services;

public sealed class ProtectionQueryService : IProtectionQueryService
{
    private readonly TradingDbContext _db;

    public ProtectionQueryService(TradingDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ProtectionDayResponse>> GetDaysAsync(string? runId, CancellationToken ct)
    {
        IQueryable<DailyProtectionLedgerEntity> query = _db.DailyProtectionLedgers;
        if (!string.IsNullOrEmpty(runId))
            query = query.Where(d => d.RunId == runId);

        return await query.OrderBy(d => d.Date).Select(d => new ProtectionDayResponse
        {
            Id = d.Id,
            Date = d.Date,
            StartEquity = d.StartEquity,
            MinEquity = d.MinEquity,
            EndEquity = d.EndEquity,
            MaxDailyDdUsedFraction = d.MaxDailyDdUsedFraction,
            FinalGovernorState = d.FinalGovernorState,
            BreachOccurred = d.BreachOccurred,
            TradesOpened = d.TradesOpened,
            TradesClosed = d.TradesClosed,
            SignalsBlocked = d.SignalsBlocked,
        }).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ProtectionEntryResponse>> GetDayDetailsAsync(DateTime date, string? runId, CancellationToken ct)
    {
        IQueryable<DailyProtectionLedgerEntity> query = _db.DailyProtectionLedgers;
        if (!string.IsNullOrEmpty(runId))
            query = query.Where(d => d.RunId == runId);

        return await query
            .Where(d => d.Date.Date == date.Date)
            .SelectMany(d => _db.ProtectionLedgerEntries
                .Where(e => e.LedgerId == d.Id)
                .OrderBy(e => e.AtUtc)
                .Select(e => new ProtectionEntryResponse
                {
                    AtUtc = e.AtUtc,
                    Category = e.Category,
                    Reason = e.Reason,
                    EquityAtTime = e.EquityAtTime,
                    DailyDdUsedFraction = e.DailyDdUsedFraction,
                }))
            .ToListAsync(ct);
    }
}
