using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Risk.Compliance;
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
    private readonly ISymbolInfoRegistry _symbols;
    private readonly IPassProbabilityEstimator _passEstimator;

    public ExitLabController(TradingDbContext db, IExcursionRepository excursions, ISymbolInfoRegistry symbols, IPassProbabilityEstimator passEstimator)
    {
        _db = db;
        _excursions = excursions;
        _symbols = symbols;
        _passEstimator = passEstimator;
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

            if (trade.InitialStopLoss is not > 0) continue;
            var sym = trade.Symbol;
            if (!_symbols.TryGet(new Symbol(sym), out var si)) continue;

            var points = ParsePoints(pathJson);
            if (points.Count == 0) continue;

            inputs.Add(new TradeExcursionInput
            {
                Direction = trade.Direction == "Short" ? TradeDirection.Short : TradeDirection.Long,
                EntryPrice = trade.EntryPrice,
                InitialStopLoss = new Price(trade.InitialStopLoss!.Value),
                PipSize = si.PipSize,
                SpreadPips = (double)si.TypicalSpread / (double)si.PipSize,
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
            Cells = cells.Select(c =>
            {
                var passProb = ComputeExitLabPassProbability(c.Result.TradeRValues);
                return new ExitLabCellResponse
                {
                    Rule = c.Rule,
                    TradeCount = c.Result.TradeCount,
                    WinRate = c.Result.WinRate,
                    AvgR = c.Result.AvgR,
                    MedianR = c.Result.MedianR,
                    AvgHoldBars = c.Result.AvgHoldBars,
                    MaxDdContributionR = c.Result.MaxDrawdownContributionR,
                    TradeRValues = c.Result.TradeRValues,
                    PassProbability = passProb,
                };
            }).ToList(),
            DefaultSlMultiples = slMultiples,
            DefaultTpMultiples = tpMultiples,
        });
    }

    /// <summary>P3.4 — save a calibrated exit rule for a (strategy, symbol, timeframe, regime) cell.</summary>
    [HttpPost("calibrations")]
    public async Task<IActionResult> SaveCalibration(SaveCalibrationRequest req, CancellationToken ct)
    {
        var regime = string.IsNullOrEmpty(req.Regime) ? null : req.Regime;

        // Upsert: remove any existing row with the same key, then insert.
        if (regime is null)
        {
            var existingNull = await _db.ExitCalibrations
                .Where(e => e.StrategyId == req.StrategyId
                    && e.Symbol == req.Symbol
                    && e.EntryTimeframe == req.EntryTimeframe
                    && e.Regime == null)
                .ToListAsync(ct);
            _db.ExitCalibrations.RemoveRange(existingNull);
        }
        else
        {
            var existing = await _db.ExitCalibrations
                .Where(e => e.StrategyId == req.StrategyId
                    && e.Symbol == req.Symbol
                    && e.EntryTimeframe == req.EntryTimeframe
                    && e.Regime == regime)
                .ToListAsync(ct);
            _db.ExitCalibrations.RemoveRange(existing);
        }

        _db.ExitCalibrations.Add(new ExitCalibrationEntity
        {
            Id = Guid.NewGuid(),
            StrategyId = req.StrategyId,
            Symbol = req.Symbol,
            EntryTimeframe = req.EntryTimeframe,
            Regime = regime,
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

    private double ComputeExitLabPassProbability(IReadOnlyList<double> rValues)
    {
        if (rValues.Count == 0) return 0.0;

        var riskPct = 0.005; // 0.5% risk per trade — standard calibration assumption
        var dailyPnL = rValues.Select(r => (decimal)(r * riskPct * 100_000)).ToList();
        var initialBalance = 100_000m;
        var currentEquity = initialBalance + dailyPnL.Sum();

        var input = new PassProbabilityInput
        {
            CurrentEquity = currentEquity,
            InitialBalance = initialBalance,
            ProfitTargetPercent = 0.10,
            MaxDailyLossPercent = 0.05,
            MaxTotalLossPercent = 0.10,
            DaysRemaining = Math.Max(1, 30 - dailyPnL.Count),
            HistoricalDailyPnL = dailyPnL,
            MonteCarloRuns = 2_000,
            DailyDdBase = DailyDdBase.InitialBalance,
        };

        return _passEstimator.Estimate(input).ProbabilityOfPass;
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
