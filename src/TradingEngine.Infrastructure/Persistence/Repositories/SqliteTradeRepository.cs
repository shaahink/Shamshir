namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteTradeRepository(TradingDbContext db) : ITradeRepository
{
    public async Task SaveAsync(TradeResult trade, string runId, CancellationToken ct)
    {
        var entity = new TradeResultEntity
        {
            Id = trade.Id,
            PositionId = trade.PositionId,
            OrderId = trade.OrderId,
            Symbol = trade.Symbol.ToString(),
            Direction = trade.Direction.ToString(),
            Lots = trade.Lots,
            EntryPrice = trade.EntryPrice.Value,
            ExitPrice = trade.ExitPrice.Value,
            StopLoss = trade.StopLoss.Value,
            TakeProfit = trade.TakeProfit?.Value,
            OpenedAtUtc = trade.OpenedAtUtc,
            ClosedAtUtc = trade.ClosedAtUtc,
            GrossPnLAmount = trade.GrossPnL.Amount,
            GrossPnLCurrency = trade.GrossPnL.Currency,
            CommissionAmount = trade.Commission.Amount,
            CommissionCurrency = trade.Commission.Currency,
            SwapAmount = trade.Swap.Amount,
            SwapCurrency = trade.Swap.Currency,
            NetPnLAmount = trade.NetPnL.Amount,
            NetPnLCurrency = trade.NetPnL.Currency,
            PnLPips = trade.PnLPips.Value,
            RMultiple = trade.RMultiple,
            MaxAdverseExcursion = trade.MaxAdverseExcursion.Value,
            MaxFavorableExcursion = trade.MaxFavorableExcursion.Value,
            ExitReason = trade.ExitReason,
            StrategyId = trade.StrategyId,
            RiskProfileId = trade.RiskProfileId,
            Mode = trade.Mode.ToString(),
            DurationSeconds = trade.DurationSeconds,
            RunId = string.IsNullOrEmpty(runId) ? null : runId,
        };
        db.Trades.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TradeResult>> GetByDateRangeAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var entities = await db.Trades
            .Where(t => t.ClosedAtUtc >= from && t.ClosedAtUtc <= to)
            .OrderByDescending(t => t.ClosedAtUtc)
            .ToListAsync(ct);
        return entities.Select(MapToDomain).ToList();
    }

    public async Task<IReadOnlyList<TradeResult>> GetByStrategyAsync(string strategyId, CancellationToken ct)
    {
        var entities = await db.Trades
            .Where(t => t.StrategyId == strategyId)
            .OrderByDescending(t => t.ClosedAtUtc)
            .ToListAsync(ct);
        return entities.Select(MapToDomain).ToList();
    }

    private static TradeResult MapToDomain(TradeResultEntity e)
    {
        return new TradeResult(
            e.Id, e.PositionId, Symbol.Parse(e.Symbol),
            Enum.Parse<TradeDirection>(e.Direction),
            e.Lots, new Price(e.EntryPrice), new Price(e.ExitPrice),
            new Price(e.StopLoss),
            e.TakeProfit.HasValue ? new Price(e.TakeProfit.Value) : null,
            e.OpenedAtUtc, e.ClosedAtUtc,
            new Money(e.GrossPnLAmount, e.GrossPnLCurrency),
            new Money(e.CommissionAmount, e.CommissionCurrency),
            new Money(e.SwapAmount, e.SwapCurrency),
            new Money(e.NetPnLAmount, e.NetPnLCurrency),
            new Pips(e.PnLPips), e.RMultiple,
            new Pips(e.MaxAdverseExcursion), new Pips(e.MaxFavorableExcursion),
            e.ExitReason, e.StrategyId, e.RiskProfileId,
            Enum.Parse<EngineMode>(e.Mode));
    }
}
