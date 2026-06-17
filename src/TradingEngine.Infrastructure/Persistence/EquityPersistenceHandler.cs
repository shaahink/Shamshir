using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Persistence;

public sealed class EquityPersistenceHandler : IEventHandler<EquityUpdated>, IAsyncDisposable
{
    private readonly PersistenceService _persistence;
    private readonly ILogger<EquityPersistenceHandler> _logger;
    private readonly Channel<(EquitySnapshot Snapshot, string RunId)> _channel =
        Channel.CreateBounded<(EquitySnapshot, string)>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false
        });
    private readonly Task _flushTask;
    private readonly CancellationTokenSource _cts = new();

    public EquityPersistenceHandler(PersistenceService persistence, ILogger<EquityPersistenceHandler> logger)
    {
        _persistence = persistence;
        _logger = logger;
        _flushTask = FlushLoopAsync(_cts.Token);
    }

    public async Task HandleAsync(EquityUpdated evt, CancellationToken ct)
    {
        await _channel.Writer.WriteAsync((evt.Snapshot, evt.RunId), ct);
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var buffer = new List<(EquitySnapshot Snapshot, string RunId)>(100);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5_000, ct);
                buffer.Clear();
                while (_channel.Reader.TryRead(out var item) && buffer.Count < 100)
                    buffer.Add(item);
                if (buffer.Count > 0)
                {
                    _logger.LogDebug("Flushing {Count} equity snapshots", buffer.Count);
                    var snapshots = buffer.Select(b => b.Snapshot).ToList();
                    var runId = buffer[0].RunId;
                    await _persistence.SaveEquitySnapshotsBatchAsync(snapshots, runId, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Equity persistence flush failed");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _flushTask; } catch { }
        _cts.Dispose();
    }
}
