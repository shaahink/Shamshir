using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Web.Dtos.Trades;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/trades")]
public sealed class TradesController : ControllerBase
{
    private readonly TradingDbContext _db;
    private readonly IBarQueryService _bars;
    private readonly IExcursionRepository _excursions;

    public TradesController(TradingDbContext db, IBarQueryService bars, IExcursionRepository excursions)
    {
        _db = db;
        _bars = bars;
        _excursions = excursions;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? strategyId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var query = _db.Trades.AsNoTracking().AsQueryable();
        if (from.HasValue) query = query.Where(t => t.ClosedAtUtc >= from.Value);
        if (to.HasValue) query = query.Where(t => t.ClosedAtUtc <= to.Value);
        if (!string.IsNullOrEmpty(strategyId)) query = query.Where(t => t.StrategyId == strategyId);

        var totalCount = await query.CountAsync(ct);
        var trades = await query
            .OrderByDescending(t => t.ClosedAtUtc)
            .Skip(skip).Take(Math.Clamp(take, 1, 200))
            .Select(t => new TradeSummaryResponse
            {
                Id = t.Id,
                PositionId = t.PositionId,
                OrderId = t.OrderId,
                RunId = t.RunId,
                Symbol = t.Symbol,
                Direction = t.Direction,
                Lots = t.Lots,
                EntryPrice = t.EntryPrice,
                ExitPrice = t.ExitPrice,
                OpenedAtUtc = t.OpenedAtUtc,
                ClosedAtUtc = t.ClosedAtUtc,
                GrossPnLAmount = t.GrossPnLAmount,
                CommissionAmount = t.CommissionAmount,
                SwapAmount = t.SwapAmount,
                NetPnLAmount = t.NetPnLAmount,
                PnLPips = t.PnLPips,
                RMultiple = t.RMultiple,
                MaxAdverseExcursion = t.MaxAdverseExcursion,
                MaxFavorableExcursion = t.MaxFavorableExcursion,
                MaeR = t.MaeR,
                MfeR = t.MfeR,
                ExitReason = t.ExitReason,
                StrategyId = t.StrategyId,
                DurationSeconds = t.DurationSeconds,
                EntryType = t.Mode,
            })
            .ToListAsync(ct);

        return Ok(new { totalCount, trades });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var t = await _db.Trades.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (t is null) return NotFound(new { error = $"Trade {id} not found" });

        // The trade's bars live at the run's timeframe; resolve it so the chart asks for the right bars.
        var timeframe = await _db.BacktestRuns
            .Where(r => r.RunId == t.RunId)
            .Select(r => r.Period)
            .FirstOrDefaultAsync(ct);

        // X3: prev/next within the run, in OpenedAtUtc order (Id tiebreak), for chart navigation.
        Guid? prevId = null, nextId = null;
        int tradeIndex = 0, tradeCount = 0;
        if (t.RunId is not null)
        {
            var runTradeIds = await _db.Trades
                .AsNoTracking()
                .Where(x => x.RunId == t.RunId)
                .OrderBy(x => x.OpenedAtUtc).ThenBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(ct);
            tradeCount = runTradeIds.Count;
            var idx = runTradeIds.IndexOf(t.Id);
            if (idx >= 0)
            {
                tradeIndex = idx + 1;
                if (idx > 0) prevId = runTradeIds[idx - 1];
                if (idx < runTradeIds.Count - 1) nextId = runTradeIds[idx + 1];
            }
        }

        return Ok(new TradeDetailResponse
        {
            Timeframe = string.IsNullOrWhiteSpace(timeframe) ? "H1" : timeframe.ToUpperInvariant(),
            RunId = t.RunId,
            PrevTradeId = prevId,
            NextTradeId = nextId,
            TradeIndex = tradeIndex,
            TradeCount = tradeCount,
            Id = t.Id,
            PositionId = t.PositionId,
            OrderId = t.OrderId,
            Symbol = t.Symbol,
            Direction = t.Direction,
            Lots = t.Lots,
            EntryPrice = t.EntryPrice,
            ExitPrice = t.ExitPrice,
            StopLoss = t.StopLoss,
            TakeProfit = t.TakeProfit,
            OpenedAtUtc = t.OpenedAtUtc,
            ClosedAtUtc = t.ClosedAtUtc,
            GrossPnLAmount = t.GrossPnLAmount,
            CommissionAmount = t.CommissionAmount,
            SwapAmount = t.SwapAmount,
            NetPnLAmount = t.NetPnLAmount,
            PnLPips = t.PnLPips,
            RMultiple = t.RMultiple,
            MaxAdverseExcursion = t.MaxAdverseExcursion,
            MaxFavorableExcursion = t.MaxFavorableExcursion,
            MaeR = t.MaeR,
            MfeR = t.MfeR,
            ExitReason = t.ExitReason,
            StrategyId = t.StrategyId,
            DurationSeconds = t.DurationSeconds,
            EntryReason = t.EntryReason,
            EntryRegime = t.EntryRegime,
            EntrySnapshotJson = t.EntrySnapshotJson,
            ExitDetailJson = t.ExitDetailJson,
        });
    }

    // iter-redesign P6.2 / X3: candlestick context window around a trade (N bars before entry through
    // N bars after exit, default 20) + entry/exit/SL/TP markers + the stop's BREAKEVEN/TRAIL path from
    // the journal. Bars come from the run's timeframe (resolved from BacktestRuns).
    [HttpGet("{id:guid}/chart")]
    public async Task<IActionResult> GetChart(Guid id, [FromQuery] int padBars = 20, CancellationToken ct = default)
    {
        var t = await _db.Trades.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound(new { error = $"Trade {id} not found" });

        var timeframeRaw = await _db.BacktestRuns
            .Where(r => r.RunId == t.RunId)
            .Select(r => r.Period)
            .FirstOrDefaultAsync(ct);
        var timeframe = string.IsNullOrWhiteSpace(timeframeRaw) ? "H1" : timeframeRaw.ToUpperInvariant();

        var pad = ParseTimeframe(timeframe) * Math.Clamp(padBars, 1, 500);
        var from = t.OpenedAtUtc - pad;
        var to = t.ClosedAtUtc + pad;

        var bars = await _bars.GetBarsAsync(t.Symbol, timeframe, from, to, ct);

        // X3: TradeResults.StopLoss is the FINAL stop (post-BE/trail) — painting it at entry time is a
        // lie (for a trailed short it sits below the entry). The stop at entry is InitialStopLoss (M34);
        // pre-M34 rows fall back to the final stop, which is then at least honest for unmanaged trades.
        var entrySl = t.InitialStopLoss is { } isl && isl > 0m ? isl : t.StopLoss;

        var markers = new List<ChartMarker>
        {
            new() { Time = Unix(t.OpenedAtUtc), Price = t.EntryPrice, Kind = "Entry" },
            new() { Time = Unix(t.ClosedAtUtc), Price = t.ExitPrice, Kind = "Exit" },
            new() { Time = Unix(t.OpenedAtUtc), Price = entrySl, Kind = "StopLoss" },
        };
        if (t.TakeProfit is { } tp && tp > 0m)
            markers.Add(new ChartMarker { Time = Unix(t.OpenedAtUtc), Price = tp, Kind = "TakeProfit" });

        var stopPath = await BuildStopPathAsync(t.RunId, t.PositionId, t.OpenedAtUtc, t.ClosedAtUtc, entrySl, ct);

        return Ok(new TradeChartResponse
        {
            TradeId = t.Id,
            Symbol = t.Symbol,
            Timeframe = timeframe,
            Direction = t.Direction,
            Bars = bars.ToList(),
            Markers = markers,
            StopPath = stopPath,
        });
    }

    // X3: replay the stop's movement for one position from the journal. BREAKEVEN/TRAIL StepRecords
    // carry a PascalCase StopLossModifyRequested event: {"PositionId":..,"NewStopLoss":{"Value":..},..}.
    private async Task<List<StopPathPoint>> BuildStopPathAsync(
        string? runId, Guid positionId, DateTime openedAtUtc, DateTime closedAtUtc, decimal initialSl, CancellationToken ct)
    {
        var path = new List<StopPathPoint>();
        if (initialSl > 0m)
            path.Add(new StopPathPoint { Time = Unix(openedAtUtc), Price = initialSl, Kind = "SL" });
        if (runId is null) return path;

        var entries = await _db.JournalEntries
            .AsNoTracking()
            .Where(j => j.RunId == runId
                && (j.EventKind == "BREAKEVEN" || j.EventKind == "TRAIL")
                && j.SimTimeUtc >= openedAtUtc && j.SimTimeUtc <= closedAtUtc)
            .OrderBy(j => j.Seq)
            .Select(j => new { j.SimTimeUtc, j.EventKind, j.EventJson })
            .ToListAsync(ct);

        foreach (var e in entries)
        {
            var move = ParseStopMove(e.EventJson, positionId);
            if (move is { } price)
                path.Add(new StopPathPoint { Time = Unix(e.SimTimeUtc), Price = price, Kind = e.EventKind });
        }
        return path;
    }

    internal static decimal? ParseStopMove(string eventJson, Guid positionId)
    {
        if (string.IsNullOrEmpty(eventJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(eventJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("PositionId", out var pid) && !root.TryGetProperty("positionId", out pid))
                return null;
            if (!Guid.TryParse(pid.GetString(), out var parsed) || parsed != positionId)
                return null;
            if (!root.TryGetProperty("NewStopLoss", out var sl) && !root.TryGetProperty("newStopLoss", out sl))
                return null;
            // Price is a value object — serialized as {"Value":1.2345}; tolerate a bare number too.
            if (sl.ValueKind == System.Text.Json.JsonValueKind.Object
                && (sl.TryGetProperty("Value", out var v) || sl.TryGetProperty("value", out v))
                && v.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                return v.GetDecimal();
            }

            if (sl.ValueKind == System.Text.Json.JsonValueKind.Number)
                return sl.GetDecimal();
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static long Unix(DateTime dt) =>
        new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static TimeSpan ParseTimeframe(string tf) => tf switch
    {
        "M1" => TimeSpan.FromMinutes(1),
        "M5" => TimeSpan.FromMinutes(5),
        "M15" => TimeSpan.FromMinutes(15),
        "M30" => TimeSpan.FromMinutes(30),
        "H1" => TimeSpan.FromHours(1),
        "H4" => TimeSpan.FromHours(4),
        "D1" => TimeSpan.FromDays(1),
        _ => TimeSpan.FromHours(1),
    };

    // P3.5 — per-bar excursion path (MAE/MFE at each fine bar) for one trade.
    [HttpGet("{id:guid}/excursions")]
    public async Task<IActionResult> GetExcursions(Guid id, CancellationToken ct)
    {
        var t = await _db.Trades.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return NotFound(new { error = $"Trade {id} not found" });
        if (t.RunId is not { } runId) return Ok(new TradeExcursionResponse { TradeId = id, PathJson = "[]" });

        var pathJson = await _excursions.GetAsync(runId, t.PositionId, ct);
        if (pathJson is null)
            return Ok(new TradeExcursionResponse { TradeId = id, PathJson = "[]" });

        return Ok(new TradeExcursionResponse { TradeId = id, PathJson = pathJson });
    }
}
