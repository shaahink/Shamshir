using TradingEngine.Domain;
using TradingEngine.Domain.Experiments;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Risk.Compliance;
using TradingEngine.Services;
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

        // Load excursion paths for the requested trades.
        // P6.4: compute trading-session regime per trade via SessionDetector on OpenedAtUtc,
        // accumulate a regime breakdown, and optionally filter by the requested regime.
        var inputs = new List<TradeExcursionInput>();
        var regimeBreakdown = new Dictionary<string, int>();
        var malformedCount = 0;
        var filteredByRegime = 0;
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

            IReadOnlyList<ExcursionPoint> points;
            try
            {
                points = ExcursionPathCodec.Parse(pathJson);
            }
            catch
            {
                malformedCount++;
                continue;
            }
            if (points.Count == 0) continue;

            // P6.4: label the entry bar's trading session
            var regime = SessionDetector.Detect(trade.OpenedAtUtc);
            regimeBreakdown[regime] = regimeBreakdown.TryGetValue(regime, out var c) ? c + 1 : 1;

            // P6.4: optional per-regime filtering
            if (req.Regime is not null && !string.Equals(regime, req.Regime, StringComparison.OrdinalIgnoreCase))
            {
                filteredByRegime++;
                continue;
            }

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
                MalformedPathCount = malformedCount,
                DefaultSlMultiples = slMultiples, DefaultTpMultiples = tpMultiples,
                Regime = req.Regime,
                RegimeBreakdown = regimeBreakdown.Count > 0 ? regimeBreakdown : null,
            });
        }

        var rules = ExitGridEvaluator.GenerateGrid(req.ReferenceAtrPips, slMultiples, tpMultiples, beTriggers, trailMultiples).ToList();
        var cells = ExitGridEvaluator.Evaluate(inputs, rules);

        // P4.5.7: plateau highlighting — mark the cell closest to the center of the top-performing
        // neighborhood as the plateau center. The user should pick plateau centers, not isolated peaks
        // (QUANT-ROADMAP §3.2 anti-overfit rule). Uses AvgR as the ranking metric.
        var cellResponses = cells.Select(c =>
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
                IsPlateauCenter = false,
            };
        }).ToList();

        MarkPlateauCenter(cellResponses);

        return Ok(new ExitLabEvaluateResponse
        {
            TotalTrades = inputs.Count,
            TotalCells = cells.Count,
            MalformedPathCount = malformedCount,
            Cells = cellResponses,
            DefaultSlMultiples = slMultiples,
            DefaultTpMultiples = tpMultiples,
            Regime = req.Regime,
            RegimeBreakdown = regimeBreakdown.Count > 0 ? regimeBreakdown : null,
        });
    }

    /// <summary>P3.4 — save a calibrated exit rule for a (strategy, symbol, timeframe, regime) cell.</summary>
    [HttpPost("calibrations")]
    public async Task<IActionResult> SaveCalibration(SaveCalibrationRequest req, CancellationToken ct)
    {
        // P4.5.4: normalise the timeframe string so "h1" / "H1" / "h1 " all map to the enum's ToString()
        // (which is always "H1"). The lookup uses e.EntryTimeframe == timeframe.ToString() case-sensitively.
        if (!Enum.TryParse<Timeframe>(req.EntryTimeframe, ignoreCase: true, out var tf))
            return BadRequest(new { error = $"Invalid timeframe '{req.EntryTimeframe}'. Must be one of: {string.Join(", ", Enum.GetNames<Timeframe>())}" });

        var normTf = tf.ToString();
        var regime = string.IsNullOrEmpty(req.Regime) ? null : req.Regime;

        // Upsert: remove any existing row with the same key, then insert.
        if (regime is null)
        {
            var existingNull = await _db.ExitCalibrations
                .Where(e => e.StrategyId == req.StrategyId
                    && e.Symbol == req.Symbol
                    && e.EntryTimeframe == normTf
                    && e.Regime == null)
                .ToListAsync(ct);
            _db.ExitCalibrations.RemoveRange(existingNull);
        }
        else
        {
            var existing = await _db.ExitCalibrations
                .Where(e => e.StrategyId == req.StrategyId
                    && e.Symbol == req.Symbol
                    && e.EntryTimeframe == normTf
                    && e.Regime == regime)
                .ToListAsync(ct);
            _db.ExitCalibrations.RemoveRange(existing);
        }

        _db.ExitCalibrations.Add(new ExitCalibrationEntity
        {
            Id = Guid.NewGuid(),
            StrategyId = req.StrategyId,
            Symbol = req.Symbol,
            EntryTimeframe = normTf,
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

    /// <summary>P4.5.7 — fetch all excursion paths for a run (replaces hand-typed GUID-pair flow).</summary>
    [HttpGet("runs/{runId}/excursions")]
    public async Task<IActionResult> GetRunExcursions(string runId, CancellationToken ct)
    {
        var paths = await _excursions.GetByRunAsync(runId, ct);
        return Ok(new { runId, count = paths.Count, paths });
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

    // P4.5.7: find the plateau center — the cell nearest the center of the top-performing region.
    // Avoids isolated peaks by requiring the plateau center to have at least one neighbor within 10%
    // of its own AvgR. When a plateau exists, the cell closest to the geometric center of the plateau
    // region is marked (the most stable parameter choice, per QUANT-ROADMAP §3.2).
    private static void MarkPlateauCenter(List<ExitLabCellResponse> cells)
    {
        if (cells.Count == 0) return;

        var bestAvgR = cells.Max(c => c.AvgR);
        if (bestAvgR <= 0) return;

        const double plateauThreshold = 0.90; // cells within 90% of the best AvgR form the plateau
        var threshold = bestAvgR * plateauThreshold;
        var plateauCells = cells.Where(c => c.AvgR >= threshold).ToList();
        if (plateauCells.Count <= 1) return;

        var slVals = plateauCells.Select(c => c.Rule.SlAtrMultiple).Distinct().OrderBy(x => x).ToList();
        var tpVals = plateauCells.Select(c => c.Rule.TpRrMultiple ?? 0).Distinct().OrderBy(x => x).ToList();
        if (slVals.Count == 0 || tpVals.Count == 0) return;

        var centerSl = slVals[slVals.Count / 2];
        var centerTp = tpVals[tpVals.Count / 2];

        var plateauCenter = plateauCells
            .OrderBy(c =>
            {
                var tp = c.Rule.TpRrMultiple ?? 0;
                var slDist = slVals.Count > 1 ? (c.Rule.SlAtrMultiple - centerSl) / (slVals[^1] - slVals[0]) : 0;
                var tpDist = tpVals.Count > 1 ? (tp - centerTp) / (tpVals[^1] - tpVals[0]) : 0;
                return slDist * slDist + tpDist * tpDist;
            })
            .FirstOrDefault();

        if (plateauCenter is not null)
        {
            var idx = cells.IndexOf(plateauCenter);
            if (idx >= 0)
                cells[idx] = plateauCenter with { IsPlateauCenter = true };
        }
    }

    private double ComputeExitLabPassProbability(IReadOnlyList<double> rValues)
    {
        if (rValues.Count == 0) return 0.0;

        // P4.5.5: fresh-challenge framing — start from initial balance, full 30-day challenge.
        // Per-trade R values are converted to dollar PnL at 0.5% risk on 100k and sampled as
        // daily PnL (one trade = one day for simplicity). The old code started from an equity
        // that already included every trade (mid-challenge) with DaysRemaining = 30 - tradeCount
        // (unit nonsense: trades≠days).
        var riskPct = 0.005;
        var initialBalance = 100_000m;
        var dailyPnL = rValues.Select(r => (decimal)(r * riskPct) * initialBalance).ToList();

        var input = new PassProbabilityInput
        {
            CurrentEquity = initialBalance,
            InitialBalance = initialBalance,
            ProfitTargetPercent = 0.10,
            MaxDailyLossPercent = 0.05,
            MaxTotalLossPercent = 0.10,
            DaysRemaining = 30,
            HistoricalDailyPnL = dailyPnL,
            MonteCarloRuns = 2_000,
            DailyDdBase = DailyDdBase.InitialBalance,
        };

        return _passEstimator.Estimate(input).ProbabilityOfPass;
    }

    /// <summary>
    /// P6.8 — evaluate pyramiding (structured adds at R-levels) against recorded excursion paths.
    /// POST /api/exit-lab/pyramid-eval { runId, strategyId?, minTrades, addLevels? }
    /// Walks each trade's excursion path, simulates an add-entry at each add-at-R threshold,
    /// and returns per-level aggregate stats: trigger rate, improvement, breakeven rate.
    /// </summary>
    [HttpPost("pyramid-eval")]
    public async Task<IActionResult> PyramidEval(PyramidEvalRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RunId))
            return BadRequest(new { error = "runId is required." });

        var addLevels = req.AddLevels ?? PyramidDiagnosis.DefaultAddLevels;
        if (addLevels.Length == 0)
            return BadRequest(new { error = "addLevels must not be empty." });

        // Load trades for the run
        var tradesQuery = _db.Trades.AsNoTracking()
            .Where(t => t.RunId == req.RunId);
        if (!string.IsNullOrEmpty(req.StrategyId))
            tradesQuery = tradesQuery.Where(t => t.StrategyId == req.StrategyId);

        var trades = await tradesQuery
            .OrderBy(t => t.OpenedAtUtc)
            .ToListAsync(ct);

        if (trades.Count < req.MinTrades)
        {
            return Ok(new PyramidEvalResponse
            {
                TotalTrades = trades.Count,
                Levels = [],
            });
        }

        // Load excursion paths
        var inputs = new List<(TradeExcursionInput trade, double riskPips)>();
        var skipped = 0;
        foreach (var trade in trades)
        {
            var pathJson = await _excursions.GetAsync(req.RunId, trade.PositionId, ct);
            if (pathJson is null) { skipped++; continue; }

            if (trade.InitialStopLoss is not > 0) { skipped++; continue; }
            if (!_symbols.TryGet(new Symbol(trade.Symbol), out var si)) { skipped++; continue; }

            IReadOnlyList<ExcursionPoint> points;
            try { points = ExcursionPathCodec.Parse(pathJson); }
            catch { skipped++; continue; }
            if (points.Count == 0) { skipped++; continue; }

            var direction = trade.Direction == "Short" ? TradeDirection.Short : TradeDirection.Long;
            var entryPrice = trade.EntryPrice;
            var stopPrice = trade.InitialStopLoss!.Value;
            var riskPips = Math.Abs((double)(entryPrice - stopPrice) / (double)si.PipSize);

            if (riskPips <= 0) { skipped++; continue; }

            inputs.Add((new TradeExcursionInput
            {
                Direction = direction,
                EntryPrice = entryPrice,
                InitialStopLoss = new Price(stopPrice),
                PipSize = si.PipSize,
                SpreadPips = (double)si.TypicalSpread / (double)si.PipSize,
                Path = points,
            }, riskPips));
        }

        if (inputs.Count < req.MinTrades)
        {
            return Ok(new PyramidEvalResponse
            {
                TotalTrades = trades.Count,
                Skipped = skipped,
                Levels = [],
            });
        }

        // Run pyramid diagnosis for each add level
        var levels = new List<PyramidLevelResponse>();
        foreach (var addLevel in addLevels)
        {
            var outcomes = new List<PyramidOutcome>();
            foreach (var (input, riskPips) in inputs)
            {
                var path = input.Path
                    .Select(p => new PyramidPathPoint(p.MinutesSinceEntry, p.HiPips, p.LoPips))
                    .ToList();

                var trial = new PyramidTrial
                {
                    AddAtR = addLevel,
                    RiskPips = riskPips,
                    TpMultiple = null, // use end-of-data exits; real TP is modelled by the path itself
                    Direction = input.Direction,
                };

                var outcome = PyramidDiagnosis.Evaluate(trial, path);
                outcomes.Add(outcome);
            }

            var summary = PyramidDiagnosis.Summarize(addLevel, outcomes);
            levels.Add(new PyramidLevelResponse
            {
                AddAtR = summary.AddAtR,
                TotalTrades = summary.TotalTrades,
                Triggered = summary.Triggered,
                TriggerRate = Math.Round(summary.TriggerRate, 4),
                Improved = summary.Improved,
                ImprovedRate = Math.Round(summary.ImprovedRate, 4),
                Breakeven = summary.Breakeven,
                BreakevenRate = Math.Round(summary.BreakevenRate, 4),
                Worsened = summary.Worsened,
                WorsenedRate = Math.Round(summary.WorsenedRate, 4),
                AvgBaseR = Math.Round(summary.AvgBaseR, 4),
                AvgPyramidR = Math.Round(summary.AvgPyramidR, 4),
                AvgImprovement = Math.Round(summary.AvgImprovement, 4),
            });
        }

        return Ok(new PyramidEvalResponse
        {
            TotalTrades = trades.Count,
            Skipped = skipped,
            Levels = levels,
        });
    }

}
