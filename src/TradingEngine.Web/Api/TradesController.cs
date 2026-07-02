using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Web.Dtos.Trades;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/trades")]
public sealed class TradesController : ControllerBase
{
    private readonly TradingDbContext _db;
    private readonly IBarQueryService _bars;

    public TradesController(TradingDbContext db, IBarQueryService bars)
    {
        _db = db;
        _bars = bars;
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
                ClosedAtUtc = t.ClosedAtUtc,
                GrossPnLAmount = t.GrossPnLAmount,
                CommissionAmount = t.CommissionAmount,
                SwapAmount = t.SwapAmount,
                NetPnLAmount = t.NetPnLAmount,
                PnLPips = t.PnLPips,
                RMultiple = t.RMultiple,
                MaxAdverseExcursion = t.MaxAdverseExcursion,
                MaxFavorableExcursion = t.MaxFavorableExcursion,
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

        return Ok(new TradeDetailResponse
        {
            Timeframe = string.IsNullOrWhiteSpace(timeframe) ? "H1" : timeframe.ToUpperInvariant(),
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
            ExitReason = t.ExitReason,
            StrategyId = t.StrategyId,
            DurationSeconds = t.DurationSeconds,
            EntryReason = t.EntryReason,
            EntryRegime = t.EntryRegime,
            EntrySnapshotJson = t.EntrySnapshotJson,
            ExitDetailJson = t.ExitDetailJson,
        });
    }

    // iter-redesign P6.2: candlestick window around a trade + entry/exit/SL/TP markers, so the UI can
    // render one trade's detail chart. Bars come from the run's timeframe (resolved from BacktestRuns).
    [HttpGet("{id:guid}/chart")]
    public async Task<IActionResult> GetChart(Guid id, [FromQuery] int padBars = 50, CancellationToken ct = default)
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

        var markers = new List<ChartMarker>
        {
            new() { Time = Unix(t.OpenedAtUtc), Price = t.EntryPrice, Kind = "Entry" },
            new() { Time = Unix(t.ClosedAtUtc), Price = t.ExitPrice, Kind = "Exit" },
            new() { Time = Unix(t.OpenedAtUtc), Price = t.StopLoss, Kind = "StopLoss" },
        };
        if (t.TakeProfit is { } tp && tp > 0m)
            markers.Add(new ChartMarker { Time = Unix(t.OpenedAtUtc), Price = tp, Kind = "TakeProfit" });

        return Ok(new TradeChartResponse
        {
            TradeId = t.Id,
            Symbol = t.Symbol,
            Timeframe = timeframe,
            Direction = t.Direction,
            Bars = bars.ToList(),
            Markers = markers,
        });
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
}
