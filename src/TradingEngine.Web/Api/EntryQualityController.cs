using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain.Experiments;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Services;

namespace TradingEngine.Web.Api;

/// <summary>
/// P6.7 — entry-quality decomposition: regress OOS trade R on observable-at-entry features
/// (ATR percentile, session, distance-to-EMA in ATR, squeeze age). Post-hoc computation from
/// bar data; no engine changes needed.
/// </summary>
[ApiController]
[Route("api/entry-quality")]
public sealed class EntryQualityController : ControllerBase
{
    private readonly TradingDbContext _db;
    private readonly ILogger<EntryQualityController> _logger;

    public EntryQualityController(TradingDbContext db, ILogger<EntryQualityController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/entry-quality?runId=...&strategyId=...&minTrades=10&lookbackBars=100
    /// Computes observable-at-entry features for every trade in the run, runs OLS regression
    /// RMultiple ~ AtrPercentile + EmaDistanceAtr + SqueezeAge + Session, and returns the diagnosis.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Diagnose(
        [FromQuery] string runId,
        [FromQuery] string? strategyId = null,
        [FromQuery] int minTrades = 10,
        [FromQuery] int lookbackBars = 100,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return BadRequest(new { error = "runId is required." });

        // Load trades for the run
        var query = _db.Trades.AsNoTracking()
            .Where(t => t.RunId == runId);
        if (!string.IsNullOrEmpty(strategyId))
            query = query.Where(t => t.StrategyId == strategyId);

        var trades = await query
            .OrderBy(t => t.OpenedAtUtc)
            .ToListAsync(ct);

        if (trades.Count < minTrades)
        {
            return Ok(new
            {
                error = $"Insufficient trades ({trades.Count}); need ≥{minTrades}.",
                totalTrades = trades.Count,
            });
        }

        // Collect unique (symbol, timeframe) pairs for bulk bar loading
        var cells = trades
            .Select(t => (t.Symbol, EntryTimeframe: t.EntryTimeframe ?? "H1"))
            .Distinct()
            .ToList();

        var barCache = new Dictionary<string, List<BarEntity>>();
        foreach (var cell in cells)
        {
            var from = trades
                .Where(t => t.Symbol == cell.Symbol && (t.EntryTimeframe ?? "H1") == cell.EntryTimeframe)
                .Min(t => t.OpenedAtUtc)
                .AddHours(-24 * (lookbackBars / 24 + 1));

            var to = trades
                .Where(t => t.Symbol == cell.Symbol && (t.EntryTimeframe ?? "H1") == cell.EntryTimeframe)
                .Max(t => t.OpenedAtUtc);

            var bars = await _db.Bars.AsNoTracking()
                .Where(b => b.Symbol == cell.Symbol && b.Timeframe == cell.EntryTimeframe
                    && b.OpenTimeUtc >= from && b.OpenTimeUtc <= to)
                .OrderBy(b => b.OpenTimeUtc)
                .ToListAsync(ct);

            var key = $"{cell.Symbol}|{cell.EntryTimeframe}";
            barCache[key] = bars;
        }

        // Compute features per trade
        var observations = new List<EntryObservation>();
        var skipped = 0;
        foreach (var trade in trades)
        {
            try
            {
                var tf = trade.EntryTimeframe ?? "H1";
                var key = $"{trade.Symbol}|{tf}";
                if (!barCache.TryGetValue(key, out var bars) || bars.Count == 0) continue;

                // Slice bars before entry (exclude the entry bar itself)
                var entryBarIdx = bars.FindLastIndex(b => b.OpenTimeUtc <= trade.OpenedAtUtc);
                if (entryBarIdx < 30) continue; // need at least 30 bars for ATR/EMA

                var preBars = bars.Take(entryBarIdx + 1).ToList();

                var high = preBars.Select(b => (double)b.High).ToList();
                var low = preBars.Select(b => (double)b.Low).ToList();
                var close = preBars.Select(b => (double)b.Close).ToList();

                // Features
                var atr14 = EntryDiagnosis.ComputeAtr(high, low, close, 14);
                var atrPct = atr14 > 0 ? atr14 : 0.0;

                var ema20 = EntryDiagnosis.ComputeEma(close, 20);
                var emaDist = atr14 > 0 ? ((double)trade.EntryPrice - ema20) / atr14 : 0.0;

                var squeezeAge = EntryDiagnosis.ComputeSqueezeAge(high, low, close, 20, 10);

                var session = SessionDetector.Detect(trade.OpenedAtUtc);

                // ATR percentile: compare current ATR to median ATR for this symbol/TF
                var medianAtr = await GetMedianAtrPipsAsync(trade.Symbol, tf, ct);
                var atrPercentile = medianAtr > 0 ? atrPct / medianAtr : 0.0;

                observations.Add(new EntryObservation(
                    RMultiple: trade.RMultiple,
                    AtrPercentile: atrPercentile,
                    EmaDistanceAtr: emaDist,
                    SqueezeAge: squeezeAge,
                    Session: session,
                    StrategyId: trade.StrategyId,
                    Symbol: trade.Symbol,
                    Timeframe: tf));
            }
            catch
            {
                skipped++;
            }
        }

        if (observations.Count < minTrades)
        {
            return Ok(new
            {
                error = $"After feature computation: {observations.Count} observations (skipped {skipped}); need ≥{minTrades}.",
                totalTrades = trades.Count,
                validObservations = observations.Count,
                skipped,
            });
        }

        var result = EntryDiagnosis.Diagnose(observations);

        return Ok(new
        {
            totalTrades = trades.Count,
            validObservations = observations.Count,
            skipped,
            result.RSquared,
            result.AdjustedRSquared,
            result.Observations,
            result.Parameters,
            result.Summary,
            features = result.Features.Select(f => new
            {
                f.Name,
                coefficient = Math.Round(f.Coefficient, 6),
                standardError = Math.Round(f.StandardError, 6),
                tStatistic = Math.Round(f.TStatistic, 3),
            }),
            intercept = Math.Round(result.Intercept, 6),
        });
    }

    private async Task<double> GetMedianAtrPipsAsync(string symbol, string timeframe, CancellationToken ct)
    {
        var scale = await _db.Set<ReferenceScaleEntity>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Symbol == symbol && s.EntryTimeframe == timeframe, ct);
        return scale?.MedianAtrPips ?? 0.0;
    }
}
