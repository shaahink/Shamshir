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
        try { await FlushAsync(); } catch { }
        _cts.Dispose();
    }

    /// <summary>
    /// Drain everything currently buffered and persist it, grouped by run. Safe to call while the
    /// background loop is still running — it does NOT complete the channel. Called at run end (and from
    /// <see cref="DisposeAsync"/>) so a backtest that finishes inside one 5s flush window doesn't lose
    /// its equity curve (the bug behind the empty Report/Live equity chart).
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        var buffer = new List<(EquitySnapshot Snapshot, string RunId)>();
        while (_channel.Reader.TryRead(out var item))
            buffer.Add(item);
        if (buffer.Count == 0) return;

        foreach (var group in buffer.GroupBy(b => b.RunId))
        {
            try
            {
                await _persistence.SaveEquitySnapshotsBatchAsync(
                    group.Select(b => b.Snapshot).ToList(), group.Key, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Equity final drain failed for run {RunId}", group.Key);
            }
        }
    }
}
