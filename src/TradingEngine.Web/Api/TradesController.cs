using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Web.Dtos.Trades;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/trades")]
public sealed class TradesController : ControllerBase
{
    private readonly TradingDbContext _db;

    public TradesController(TradingDbContext db)
    {
        _db = db;
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
        var query = _db.Trades.AsQueryable();
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
        });
    }
}
