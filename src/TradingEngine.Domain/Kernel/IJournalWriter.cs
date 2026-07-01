namespace TradingEngine.Domain;

/// <summary>
/// The single sink for the unified journal (iter-35 A3). Replaces the independent
/// <c>PipelineEventWriter</c> and <c>BarEvaluationHandler</c> writers (Kill-List): there must be
/// exactly one implementation in <c>src</c>.
///
/// Implementations MUST be lossless: back it with a bounded channel in <c>BoundedChannelFullMode.Wait</c>
/// (NOT <c>DropOldest</c> — C9/H17), clear the flush buffer only AFTER a successful save (H19), enable
/// SQLite WAL + busy_timeout + retry (H20/H21), and drain before cancelling on dispose (M16).
/// </summary>
public interface IJournalWriter : IAsyncDisposable
{
    /// <summary>Append one step. Lossless — blocks (briefly) rather than dropping under backpressure.</summary>
    void Append(StepRecord record);

    /// <summary>Flush all buffered records to durable storage (called at run end / on demand for export).</summary>
    Task FlushAsync(CancellationToken ct);
}
