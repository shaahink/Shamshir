namespace TradingEngine.Domain;

public interface ITradeRepository
{
    Task SaveAsync(TradeResult trade, CancellationToken ct);
    Task<IReadOnlyList<TradeResult>> GetByDateRangeAsync(DateTime from, DateTime to, CancellationToken ct);
    Task<IReadOnlyList<TradeResult>> GetByStrategyAsync(string strategyId, CancellationToken ct);
}
