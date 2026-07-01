namespace TradingEngine.Domain;

/// <summary>
/// The durable persistence boundary for the unified journal (iter-35 A3). <see cref="ChannelJournalWriter"/>
/// batches <see cref="StepRecord"/>s and hands them here. TODO(deepseek): implement the SQLite-backed sink
/// (a single <c>Journal</c> table) + the NDJSON exporter for <c>GET /api/runs/{id}/journal/export</c>, with
/// WAL + busy_timeout + retry (H20/H21) and the indices in PLAN Part D.
/// </summary>
public interface IStepRecordSink
{
    Task AppendBatchAsync(IReadOnlyList<StepRecord> batch, CancellationToken ct);
}
