using System.Threading.Channels;
using TradingEngine.Domain;

namespace TradingEngine.Engine;

/// <summary>
/// The single lossless journal writer (iter-35 A3). This is the concrete fix for the persistence bugs
/// that plagued the old PipelineEventWriter / BarEvaluationHandler:
///   • C9/H17 — the channel is <see cref="BoundedChannelFullMode.Wait"/> (NEVER DropOldest);
///   • H19    — the flush buffer is cleared ONLY after a successful sink write, with bounded retry;
///   • M16    — DisposeAsync drains the channel fully BEFORE cancelling.
/// Persistence itself is delegated to <see cref="IStepRecordSink"/> (DeepSeek writes the SQLite sink).
///
/// NOTE: lives in Engine only because it needs nothing but Domain + BCL. It is plumbing, not decision
/// logic, so it is exempt from the kernel-purity Arch test (that test targets Kernel/PreTradeGate/
/// EngineReducer). DeepSeek may relocate it to Infrastructure alongside the sink if preferred.
/// </summary>
public sealed class ChannelJournalWriter : IJournalWriter
{
    private readonly Channel<StepRecord> _channel;
    private readonly IStepRecordSink _sink;
    private readonly int _batchSize;
    private readonly Task _flushLoop;
    private readonly CancellationTokenSource _cts = new();
    private long _droppedBatches;

    /// <summary>Batches that exhausted retries and were dropped. MUST stay 0 in healthy runs; surfaced
    /// instead of silently swallowed (the old bug). TODO(deepseek): log + emit a metric on increment.</summary>
    public long DroppedBatches => Interlocked.Read(ref _droppedBatches);

    public ChannelJournalWriter(IStepRecordSink sink, int capacity = 50_000, int batchSize = 500)
    {
        _sink = sink;
        _batchSize = batchSize;
        _channel = Channel.CreateBounded<StepRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _flushLoop = FlushLoopAsync(_cts.Token);
    }

    public void Append(StepRecord record)
    {
        // Lossless: in Wait mode TryWrite only fails when full/completed. The kernel is single-threaded
        // so contention is rare; block briefly rather than drop. (Sync-over-async is acceptable here —
        // the producer is the deterministic driver loop, not a request thread.)
        if (!_channel.Writer.TryWrite(record))
        {
            _channel.Writer.WriteAsync(record).AsTask().GetAwaiter().GetResult();
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var buffer = new List<StepRecord>(_batchSize);
        try
        {
            await foreach (var rec in _channel.Reader.ReadAllAsync(ct))
            {
                buffer.Add(rec);
                // Flush when the batch is full or the channel has momentarily drained.
                if (buffer.Count >= _batchSize || !_channel.Reader.TryPeek(out _))
                {
                    await FlushBufferAsync(buffer, ct);
                }
            }
        }
        catch (OperationCanceledException) { }

        // Drain anything left after the writer completed.
        if (buffer.Count > 0)
        {
            await FlushBufferAsync(buffer, CancellationToken.None);
        }
    }

    private async Task FlushBufferAsync(List<StepRecord> buffer, CancellationToken ct)
    {
        if (buffer.Count == 0)
        {
            return;
        }
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await _sink.AppendBatchAsync(buffer, ct);
                buffer.Clear(); // H19: clear ONLY after success.
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }
                try { await Task.Delay(100 * (attempt + 1), ct); } catch { break; }
            }
        }
        // Exhausted retries — observable loss, not silent (the old failure mode).
        Interlocked.Increment(ref _droppedBatches);
        buffer.Clear();
    }

    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask; // continuous loop; DisposeAsync drains.

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _flushLoop; } catch { } // M16: drain BEFORE cancel
        _cts.Cancel();
        _cts.Dispose();
    }
}
