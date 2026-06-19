using TradingEngine.Engine;

namespace TradingEngine.Infrastructure;

/// <summary>
/// The replay engine (iter-35 A4). Given a <see cref="DatasetRef"/> + <see cref="ConfigSet"/> + seed,
/// materializes the tape and drives the kernel to completion. The same RunSpec always produces a
/// bit-identical journal — this is the system's strongest correctness guarantee.
/// </summary>
public sealed class ReplayRunner
{
    private readonly IKernel _kernel;
    private readonly IBarRepository _bars;
    private readonly IStepRecordSink _sink;

    public ReplayRunner(IKernel kernel, IBarRepository bars, IStepRecordSink sink)
    {
        _kernel = kernel;
        _bars = bars;
        _sink = sink;
    }

    public async Task<IReadOnlyList<StepRecord>> RunAsync(DatasetRef dataset, EngineState initialState, CancellationToken ct)
    {
        var tape = new BarTape(dataset, _bars);
        var queue = new InMemoryEngineEventQueue();
        var journal = new ChannelJournalWriter(_sink);
        var effects = new ReplayEffectExecutor(queue);
        var driver = new KernelDriver(_kernel, queue, journal, effects, dataset.DatasetId);

        var state = await driver.RunAsync(tape, initialState, ct);
        await journal.DisposeAsync();

        // Read back the journal from the sink.
        return await ((ReplaySinkRead)_sink).GetRecordsAsync(ct);
    }
}

/// <summary>
/// Effect executor for replay: does NOT call a real venue. Instead, simulates instant market fills
/// and re-enqueues feedback events (OrderFilled, EquityObserved) for deterministic processing.
/// </summary>
public sealed class ReplayEffectExecutor(IEngineEventQueue queue, decimal fallbackPrice = 1.0m) : IEffectExecutor
{
    public Task ExecuteAsync(EngineEffect effect, CancellationToken ct)
    {
        switch (effect)
        {
            case SubmitOrder so:
                queue.Enqueue(new OrderSubmitted(
                    so.OrderId, so.Symbol, so.Direction, so.Lots,
                    so.LimitPrice, so.StrategyId, DateTime.MinValue,
                    so.StopLoss, so.TakeProfit));
                // AF9: fill at the limit price (deterministic) or fallback — no more fill-at-zero.
                queue.Enqueue(new OrderFilled(
                    so.OrderId, so.Symbol, so.Lots,
                    so.LimitPrice ?? new Price(fallbackPrice), DateTime.MinValue));
                break;

            case CloseOpenPosition co:
                queue.Enqueue(new CloseRequested(co.OrderId, co.Reason, DateTime.MinValue));
                queue.Enqueue(new OrderFilled(
                    co.OrderId, Symbol.Parse("EURUSD"), 0.01m,
                    new Price(fallbackPrice), DateTime.MinValue));
                break;
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Buffered in-memory sink that also supports read-back for determinism tests.
/// Required because ChannelJournalWriter flushes asynchronously.
/// </summary>
public sealed class ReplaySinkRead : IStepRecordSink
{
    private readonly List<StepRecord> _records = [];
    private readonly TaskCompletionSource _ready = new();

    public Task Ready => _ready.Task;

    public IReadOnlyList<StepRecord> Records => _records;

    public async Task<IReadOnlyList<StepRecord>> GetRecordsAsync(CancellationToken ct)
    {
        await _ready.Task.WaitAsync(ct);
        return _records;
    }

    public Task AppendBatchAsync(IReadOnlyList<StepRecord> batch, CancellationToken ct)
    {
        _records.AddRange(batch);
        return Task.CompletedTask;
    }
}
