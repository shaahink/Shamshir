namespace TradingEngine.Domain;

public interface IJournalQueryRepository
{
    Task<IReadOnlyList<StepRecord>> GetByRunAsync(string runId, long? afterSeq, int limit, CancellationToken ct);
    IAsyncEnumerable<StepRecord> StreamByRunAsync(string runId, long? afterSeq, CancellationToken ct);
}
