using TradingEngine.Engine;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// Locks the lossless guarantee of the unified journal (iter-35 A3) that PLAN-FINISH-AB flagged as
/// untested. The old PipelineEventWriter/BarEvaluationHandler used DropOldest + cleared the buffer
/// before a retry could run, so events vanished silently under backpressure (C9/H17/H19/H20). The
/// kernel's <see cref="ChannelJournalWriter"/> must instead: (a) never drop under a burst, and
/// (b) retry a failed sink batch rather than lose it.
/// </summary>
[Trait("Category", "Journal")]
[Trait("Speed", "Fast")]
public sealed class JournalLosslessTests
{
    private static StepRecord Rec(long seq) => new(
        "run", seq,
        new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(seq),
        "BarClosed", "{}", Array.Empty<string>(), "[]",
        new RiskSnapshot(10_000m, 10_000m, 0m, 0m, 0m, 0m, 0m, false, null, "Normal", 0),
        null, null, Array.Empty<StrategyVerdict>());

    [Fact]
    public async Task Journal_NoDropUnderBurst()
    {
        var sink = new InMemoryStepRecordSink();
        // Capacity far smaller than the burst: the Wait-mode channel must back-pressure the producer,
        // never DropOldest.
        var writer = new ChannelJournalWriter(sink, capacity: 8, batchSize: 4);

        const int n = 500;
        for (var i = 1; i <= n; i++)
        {
            writer.Append(Rec(i));
        }

        await writer.DisposeAsync(); // drains fully before returning (M16)

        sink.Records.Should().HaveCount(n, "every record in the burst must persist (no DropOldest)");
        sink.Records.Select(r => r.Seq).Should().BeEquivalentTo(Enumerable.Range(1, n).Select(i => (long)i));
        writer.DroppedBatches.Should().Be(0);
    }

    [Fact]
    public async Task Journal_RetriesFailedBatch_NoLoss()
    {
        // The sink throws on its first batch, then succeeds. The writer must retry the SAME buffer
        // (cleared only after success) so nothing is lost — the H19/H20 failure mode.
        var sink = new FlakyStepRecordSink(failBatches: 1);
        var writer = new ChannelJournalWriter(sink, capacity: 64, batchSize: 8);

        const int n = 50;
        for (var i = 1; i <= n; i++)
        {
            writer.Append(Rec(i));
        }

        await writer.DisposeAsync();

        sink.AppendCalls.Should().BeGreaterThan(1, "the first batch failed and must have been retried");
        sink.Records.Should().HaveCount(n, "the failed batch must be retried, not dropped");
        writer.DroppedBatches.Should().Be(0);
    }
}

/// <summary>An <see cref="IStepRecordSink"/> that fails its first N batches (transient), then persists.</summary>
public sealed class FlakyStepRecordSink : IStepRecordSink
{
    private readonly object _lock = new();
    private readonly List<StepRecord> _records = [];
    private int _failuresRemaining;

    public FlakyStepRecordSink(int failBatches) => _failuresRemaining = failBatches;

    public int AppendCalls { get; private set; }

    public IReadOnlyList<StepRecord> Records
    {
        get { lock (_lock) { return _records.ToList(); } }
    }

    public Task AppendBatchAsync(IReadOnlyList<StepRecord> batch, CancellationToken ct)
    {
        lock (_lock)
        {
            AppendCalls++;
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new InvalidOperationException("transient sink failure");
            }
            _records.AddRange(batch);
        }
        return Task.CompletedTask;
    }
}
