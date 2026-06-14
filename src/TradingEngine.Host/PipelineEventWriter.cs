using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Host;

public sealed class PipelineEventWriter : IPipelineJournal, IDecisionJournal, IAsyncDisposable
{
    private readonly string _runId;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PipelineEventWriter> _logger;
    private readonly Channel<PipelineEvent> _channel =
        Channel.CreateBounded<PipelineEvent>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false
        });
    private readonly Task _flushTask;
    private readonly CancellationTokenSource _cts = new();
    private long _seq;

    public PipelineEventWriter(string runId, IServiceScopeFactory scopeFactory, ILogger<PipelineEventWriter> logger)
    {
        _runId = runId;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _flushTask = FlushLoopAsync(_cts.Token);
    }

    public void Record(DecisionRecord r)
    {
        var evt = new PipelineEvent(
            Guid.NewGuid(),
            r.RunId,
            Interlocked.Increment(ref _seq),
            r.Event,
            r.Symbol ?? r.StrategyId,
            r.SimTimeUtc,
            DateTime.UtcNow,
            r.DetailJson,
            r.PhaseBefore,
            r.PhaseAfter,
            r.GuardResult,
            r.Reason);
        _channel.Writer.TryWrite(evt);
    }

    public void Write(string stage, string? correlationId, DateTime simTime, string detailJson = "{}")
    {
        var evt = new PipelineEvent(
            Guid.NewGuid(),
            _runId,
            Interlocked.Increment(ref _seq),
            stage,
            correlationId,
            simTime,
            DateTime.UtcNow,
            detailJson);
        _channel.Writer.TryWrite(evt);
    }

    public (long seq, long barsRecv, long cmdsSent, long execsRecv) GetCounters() =>
        (Volatile.Read(ref _seq), 0, 0, 0);

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var buffer = new List<PipelineEvent>(500);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3_000, ct);
                buffer.Clear();
                while (_channel.Reader.TryRead(out var evt) && buffer.Count < 500)
                    buffer.Add(evt);
                if (buffer.Count == 0) continue;

                await using var scope = _scopeFactory.CreateAsyncScope();
                var repo = scope.ServiceProvider.GetRequiredService<IPipelineEventRepository>();
                await repo.AppendBatchAsync(buffer, ct);
                _logger.LogDebug("PIPELINE_FLUSHED|{Count}", buffer.Count);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PipelineEventWriter flush failed");
            }
        }
    }

    public async Task FlushRemainingAsync()
    {
        var remaining = new List<PipelineEvent>(1_000);
        while (_channel.Reader.TryRead(out var evt))
            remaining.Add(evt);

        if (remaining.Count == 0) return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IPipelineEventRepository>();
            await repo.AppendBatchAsync(remaining, CancellationToken.None);
            _logger.LogDebug("PipelineEventWriter: explicit pre-dispose flush {Count}", remaining.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PipelineEventWriter: pre-dispose flush failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _channel.Writer.Complete(); } catch { }
        try { _cts.Cancel(); } catch { }
        try { await _flushTask; } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
