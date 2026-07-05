using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Services.ExitLab;
using TradingEngine.Web.Dtos.ExitLab;

namespace TradingEngine.Web.Api;

/// <summary>P3.5 — exit-lab API: run grid evaluations against recorded excursion paths.</summary>
[ApiController]
[Route("api/exit-lab")]
public sealed class ExitLabController : ControllerBase
{
    private readonly TradingDbContext _db;
    private readonly IExcursionRepository _excursions;

    public ExitLabController(TradingDbContext db, IExcursionRepository excursions)
    {
        _db = db;
        _excursions = excursions;
    }

    /// <summary>Evaluate a grid of exit rules against excursion paths of selected trades.</summary>
    [HttpPost("evaluate")]
    public async Task<IActionResult> Evaluate(ExitLabEvaluateRequest req, CancellationToken ct)
    {
        if (req.PositionIds.Count == 0)
            return BadRequest(new { error = "At least one PositionId is required." });

        var slMultiples = req.SlMultiples ?? ExitGridEvaluator.DefaultSlMultiples;
        var tpMultiples = req.TpMultiples ?? ExitGridEvaluator.DefaultTpMultiples;
        var beTriggers = req.BeTriggers ?? ExitGridEvaluator.DefaultBeTriggers;
        var trailMultiples = req.TrailMultiples ?? ExitGridEvaluator.DefaultTrailMultiples;

        // Load excursion paths for the requested trades
        var inputs = new List<TradeExcursionInput>();
        for (var i = 0; i < req.PositionIds.Count && i < req.RunIds.Count; i++)
        {
            var pathJson = await _excursions.GetAsync(req.RunIds[i], req.PositionIds[i], ct);
            if (pathJson is null) continue;

            var trade = await _db.Trades
                .FirstOrDefaultAsync(t => t.PositionId == req.PositionIds[i] && t.RunId == req.RunIds[i], ct);
            if (trade is null) continue;

            var points = ParsePoints(pathJson);
            if (points.Count == 0) continue;

            inputs.Add(new TradeExcursionInput
            {
                Direction = trade.Direction == "Short" ? TradeDirection.Short : TradeDirection.Long,
                EntryPrice = trade.EntryPrice,
                InitialStopLoss = trade.StopLoss > 0 ? new Price(trade.StopLoss) : new Price(trade.EntryPrice),
                PipSize = 0.0001m, // EURUSD default — derived from symbol registry in production
                SpreadPips = 2.0,
                Path = points,
            });
        }

        if (inputs.Count == 0)
        {
            return Ok(new ExitLabEvaluateResponse
            {
                TotalTrades = 0, TotalCells = 0, Cells = [],
                DefaultSlMultiples = slMultiples, DefaultTpMultiples = tpMultiples,
            });
        }

        var rules = ExitGridEvaluator.GenerateGrid(req.ReferenceAtrPips, slMultiples, tpMultiples, beTriggers, trailMultiples).ToList();
        var cells = ExitGridEvaluator.Evaluate(inputs, rules);

        return Ok(new ExitLabEvaluateResponse
        {
            TotalTrades = inputs.Count,
            TotalCells = cells.Count,
            Cells = cells.Select(c => new ExitLabCellResponse
            {
                Rule = c.Rule,
                TradeCount = c.Result.TradeCount,
                WinRate = c.Result.WinRate,
                AvgR = c.Result.AvgR,
                MedianR = c.Result.MedianR,
                AvgHoldBars = c.Result.AvgHoldBars,
                MaxDdContributionR = c.Result.MaxDrawdownContributionR,
                TradeRValues = c.Result.TradeRValues,
            }).ToList(),
            DefaultSlMultiples = slMultiples,
            DefaultTpMultiples = tpMultiples,
        });
    }

    /// <summary>P3.4 — save a calibrated exit rule for a (strategy, symbol, timeframe, regime) cell.</summary>
    [HttpPost("calibrations")]
    public async Task<IActionResult> SaveCalibration(SaveCalibrationRequest req, CancellationToken ct)
    {
        // Upsert: remove any existing row with the same key, then insert.
        var existing = await _db.ExitCalibrations
            .Where(e => e.StrategyId == req.StrategyId
                && e.Symbol == req.Symbol
                && e.EntryTimeframe == req.EntryTimeframe
                && e.Regime == req.Regime)
            .ToListAsync(ct);

        _db.ExitCalibrations.RemoveRange(existing);

        _db.ExitCalibrations.Add(new ExitCalibrationEntity
        {
            Id = Guid.NewGuid(),
            StrategyId = req.StrategyId,
            Symbol = req.Symbol,
            EntryTimeframe = req.EntryTimeframe,
            Regime = req.Regime,
            SlAtrMultiple = req.Rule.SlAtrMultiple,
            TpRrMultiple = req.Rule.TpRrMultiple,
            BeTriggerR = req.Rule.BeTriggerR,
            BeOffsetPips = req.Rule.BeOffsetPips,
            TrailAtrMultiple = req.Rule.TrailAtrMultiple,
            PartialTriggerR = req.Rule.PartialTriggerR,
            PartialCloseFraction = req.Rule.PartialCloseFraction,
            DatasetId = req.DatasetId,
            IsStartUtc = req.IsStartUtc,
            IsEndUtc = req.IsEndUtc,
            OosStartUtc = req.OosStartUtc,
            OosEndUtc = req.OosEndUtc,
            FittedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new { saved = true });
    }

    /// <summary>P3.4 — list calibrations, optionally filtered by strategy/symbol.</summary>
    [HttpGet("calibrations")]
    public async Task<IActionResult> ListCalibrations(
        [FromQuery] string? strategyId,
        [FromQuery] string? symbol,
        CancellationToken ct)
    {
        var q = _db.ExitCalibrations.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(strategyId)) q = q.Where(e => e.StrategyId == strategyId);
        if (!string.IsNullOrEmpty(symbol)) q = q.Where(e => e.Symbol == symbol);

        var rows = await q.OrderBy(e => e.StrategyId).ThenBy(e => e.Symbol).ToListAsync(ct);
        return Ok(rows);
    }

    private static List<ExcursionPoint> ParsePoints(string pathJson)
    {
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<List<double>>>(pathJson);
            if (parsed is null) return [];
            return parsed
                .Where(a => a.Count >= 3)
                .Select(a => new ExcursionPoint((int)a[0], a[1], a[2]))
                .ToList();
        }
        catch { return []; }
    }
}
