namespace TradingEngine.Domain;

public sealed record PipelineEvent(
    Guid Id,
    string RunId,
    long Seq,
    string Stage,
    string? CorrelationId,
    DateTime SimTimeUtc,
    DateTime WallTimeUtc,
    string DetailJson);

public interface IPipelineEventRepository
{
    Task AppendBatchAsync(IReadOnlyList<PipelineEvent> events, CancellationToken ct);
    Task<IReadOnlyList<PipelineEvent>> GetByRunIdAsync(string runId, CancellationToken ct);
}
