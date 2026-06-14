using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Persistence;

public sealed class EquityPersistenceHandler : IEventHandler<EquityUpdated>, IAsyncDisposable
{
    private readonly PersistenceService _persistence;
    private readonly ILogger<EquityPersistenceHandler> _logger;
    private readonly Channel<EquitySnapshot> _channel =
        Channel.CreateBounded<EquitySnapshot>(new BoundedChannelOptions(10_000)
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
        await _channel.Writer.WriteAsync(evt.Snapshot, ct);
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var buffer = new List<EquitySnapshot>(100);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5_000, ct);
                buffer.Clear();
                while (_channel.Reader.TryRead(out var snapshot) && buffer.Count < 100)
                    buffer.Add(snapshot);
                if (buffer.Count > 0)
                {
                    _logger.LogDebug("Flushing {Count} equity snapshots", buffer.Count);
                    await _persistence.SaveEquitySnapshotsBatchAsync(buffer, ct);
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
