namespace TradingEngine.Domain;

public interface IJournalQueryRepository
{
    Task<IReadOnlyList<StepRecord>> GetByRunAsync(string runId, long? afterSeq, int limit, CancellationToken ct, string? kind = null);
    IAsyncEnumerable<StepRecord> StreamByRunAsync(string runId, long? afterSeq, CancellationToken ct);
}
