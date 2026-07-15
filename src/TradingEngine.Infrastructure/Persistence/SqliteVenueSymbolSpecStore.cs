using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain.Interfaces;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence;

/// <summary>
/// P4.4 (F44): EF-backed <see cref="IVenueSymbolSpecStore"/>. The VenueSymbolSpecs table, its entity and
/// its migration (M51) all shipped with P1 — nothing ever wrote to them or read them back, so the table
/// sat empty and the tape kept pricing swap off symbols.json. This is the missing half.
/// </summary>
public sealed class SqliteVenueSymbolSpecStore : IVenueSymbolSpecStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SqliteVenueSymbolSpecStore> _logger;

    public SqliteVenueSymbolSpecStore(IServiceScopeFactory scopeFactory, ILogger<SqliteVenueSymbolSpecStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task SaveAsync(VenueSymbolSpec spec, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var symbol = spec.Symbol.ToString();
        var existing = await db.VenueSymbolSpecs
            .FirstOrDefaultAsync(x => x.Symbol == symbol && x.Broker == spec.Broker, ct);

        var entity = existing ?? new VenueSymbolSpecEntity { Symbol = symbol, Broker = spec.Broker };

        entity.CapturedAtUtc = spec.CapturedAtUtc.ToString("O");
        entity.Commission = (double)spec.Commission;
        entity.CommissionType = spec.CommissionType.ToString();
        entity.SwapLong = (double)spec.SwapLong;
        entity.SwapShort = (double)spec.SwapShort;
        entity.SwapCalculationType = spec.SwapCalculationType;
        entity.LotSize = (double)spec.LotSize;
        entity.PipSize = (double)spec.PipSize;
        entity.TickSize = (double)spec.TickSize;
        entity.TickValue = (double)spec.TickValue;
        entity.Digits = spec.Digits;
        entity.TripleSwapDay = spec.TripleSwapDay.ToString();
        entity.TypicalSpread = (double)spec.TypicalSpread;

        if (existing is null) db.VenueSymbolSpecs.Add(entity);

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "VENUE_SPEC_PERSISTED {Symbol}@{Broker} commType={CommType} comm={Comm} swapL={SwapLong} swapS={SwapShort} tripleSwap={TripleDay}",
            symbol, spec.Broker, spec.CommissionType, spec.Commission, spec.SwapLong, spec.SwapShort, spec.TripleSwapDay);
    }

    public async Task<IReadOnlyList<VenueSymbolSpec>> LoadAllAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var rows = await db.VenueSymbolSpecs.AsNoTracking().ToListAsync(ct);
        var specs = new List<VenueSymbolSpec>(rows.Count);

        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Symbol)) continue;

            specs.Add(new VenueSymbolSpec(
                Symbol.Parse(r.Symbol),
                r.Broker,
                DateTime.TryParse(r.CapturedAtUtc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.UnixEpoch,
                (decimal)r.Commission,
                Enum.TryParse<CommissionType>(r.CommissionType, out var ct2) ? ct2 : CommissionType.Unknown,
                (decimal)r.SwapLong,
                (decimal)r.SwapShort,
                r.SwapCalculationType,
                (decimal)r.LotSize,
                (decimal)r.PipSize,
                (decimal)r.TickSize,
                (decimal)r.TickValue,
                r.Digits,
                Enum.TryParse<DayOfWeek>(r.TripleSwapDay, out var d) ? d : DayOfWeek.Wednesday,
                (decimal)r.TypicalSpread));
        }

        return specs;
    }
}
