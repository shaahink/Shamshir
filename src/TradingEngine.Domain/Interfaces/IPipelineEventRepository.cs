namespace TradingEngine.Domain;

public sealed record PipelineEvent(
    Guid Id,
    string RunId,
    long Seq,
    string Stage,
    string? CorrelationId,
    DateTime SimTimeUtc,
    DateTime WallTimeUtc,
    string DetailJson,
    string? PhaseBefore = null,
    string? PhaseAfter = null,
    string? GuardResult = null,
    string? Reason = null);

public interface IPipelineEventRepository
{
    Task AppendBatchAsync(IReadOnlyList<PipelineEvent> events, CancellationToken ct);
    Task<IReadOnlyList<PipelineEvent>> GetByRunIdAsync(string runId, CancellationToken ct);
}
