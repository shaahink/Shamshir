namespace TradingEngine.Host;

/// <summary>
/// A no-op <see cref="IJournalWriter"/> placeholder for the kernel engine until K5 wires the real
/// <c>SqliteStepRecordSink</c> (via a lossless <c>ChannelJournalWriter</c>). The decision journal
/// (rejections) + trade persistence still flow through the EffectExecutor's existing sinks, so a run is
/// not journal-less in the meantime — only the rich per-step StepRecord stream is dropped here.
/// </summary>
public sealed class NullJournalWriter : IJournalWriter
{
    public void Append(StepRecord record) { }
    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
