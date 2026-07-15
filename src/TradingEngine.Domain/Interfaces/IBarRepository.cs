namespace TradingEngine.Domain;

public interface IBarRepository
{
    Task BulkInsertAsync(IReadOnlyList<Bar> bars, CancellationToken ct);
    Task BulkInsertAsync(string runId, IReadOnlyList<Bar> bars, CancellationToken ct);
    Task<IReadOnlyList<Bar>> GetAsync(Symbol symbol, Timeframe tf, DateTime from, DateTime to, CancellationToken ct);
    Task<IReadOnlyList<Bar>> GetAsync(string runId, Symbol symbol, Timeframe tf, DateTime from, DateTime to, CancellationToken ct);
}
