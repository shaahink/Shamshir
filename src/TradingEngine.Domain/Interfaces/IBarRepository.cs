namespace TradingEngine.Domain;

public interface IBarRepository
{
    Task BulkInsertAsync(IReadOnlyList<Bar> bars, CancellationToken ct);
    Task<IReadOnlyList<Bar>> GetAsync(Symbol symbol, Timeframe tf, DateTime from, DateTime to, CancellationToken ct);
}
