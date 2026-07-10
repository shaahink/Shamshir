using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Skender.Stock.Indicators;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.MarketData;

public sealed class ReferenceScalePopulator
{
    private readonly IMarketDataStore _marketData;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISymbolInfoRegistry _symbols;

    public ReferenceScalePopulator(
        IMarketDataStore marketData,
        IServiceScopeFactory scopeFactory,
        ISymbolInfoRegistry symbols)
    {
        _marketData = marketData;
        _scopeFactory = scopeFactory;
        _symbols = symbols;
    }

    public async Task<int> PopulateAllAsync(CancellationToken ct = default)
    {
        var inventory = await _marketData.GetInventoryAsync(ct);
        var groups = inventory.GroupBy(i => (i.Symbol, i.Timeframe)).ToList();
        var updated = 0;

        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ok = await PopulateOneAsync(new Symbol(g.Key.Symbol), g.Key.Timeframe, ct);
                if (ok) updated++;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip cells with corrupt/missing data */ }
        }

        return updated;
    }

    private async Task<bool> PopulateOneAsync(Symbol symbol, Timeframe tf, CancellationToken ct)
    {
        var inventory = await _marketData.GetInventoryAsync(ct);
        var entry = inventory.FirstOrDefault(i =>
            string.Equals(i.Symbol, symbol.Value, StringComparison.OrdinalIgnoreCase)
            && i.Timeframe == tf);

        if (entry is null) return false;

        var bars = await _marketData.ReadBarsAsync(symbol, tf, entry.FirstOpenUtc, entry.LastOpenUtc, ct);
        if (bars.Count < 30) return false;

        var pipSize = _symbols.TryGet(symbol, out var info) && info.PipSize > 0
            ? (double)info.PipSize
            : 0.0001;

        // Compute ATR(14) via Skender, take median of the series
        double medianAtrPips;
        {
            var quotes = bars.Select(b => new SkenderQuote(b)).ToList();
            var atrResults = quotes.GetAtr(14).Skip(13).ToList(); // skip warmup
            var atrValues = atrResults.Select(a => a.Atr ?? 0).Where(v => v > 0).ToList();
            medianAtrPips = atrValues.Count > 0 ? Median(atrValues) / pipSize : 0;
        }

        // Median bar range in pips
        double medianBarRangePips;
        {
            var ranges = bars.Select(b => (double)(b.High - b.Low) / pipSize).Where(r => r > 0).ToList();
            medianBarRangePips = ranges.Count > 0 ? Median(ranges) : 0;
        }

        // P6.2: median spread from per-bar recorded spread when available (e.g. live-tick capture);
        // falls back to the symbol's TypicalSpread for historical bars without per-bar data.
        double spreadPips;
        {
            var barSpreads = bars.Select(b => b.Spread)
                .Where(s => s.HasValue)
                .Select(s => (double)s!.Value / pipSize)
                .Where(v => v > 0)
                .ToList();
            spreadPips = barSpreads.Count > 0 ? Median(barSpreads) : (
                info is not null && info.PipSize > 0
                    ? (double)(info.TypicalSpread / info.PipSize) : 0);
        }

        if (medianAtrPips <= 0) return false;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var existing = await db.ReferenceScales.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Symbol == symbol.Value && r.EntryTimeframe == tf.ToString(), ct);

        if (existing is not null)
        {
            db.ReferenceScales.Attach(existing);
            existing.MedianAtrPips = medianAtrPips;
            existing.MedianBarRangePips = medianBarRangePips;
            existing.MedianSpreadPips = spreadPips;
            existing.SampleBarCount = bars.Count;
            existing.RefreshedAtUtc = DateTime.UtcNow;
        }
        else
        {
            db.ReferenceScales.Add(new ReferenceScaleEntity
            {
                Id = Guid.NewGuid(),
                Symbol = symbol.Value,
                EntryTimeframe = tf.ToString(),
                MedianAtrPips = medianAtrPips,
                MedianBarRangePips = medianBarRangePips,
                MedianSpreadPips = spreadPips,
                SampleBarCount = bars.Count,
                RefreshedAtUtc = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var n = sorted.Count;
        if (n == 0) return 0;
        return n % 2 == 0
            ? (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0
            : sorted[n / 2];
    }
}
