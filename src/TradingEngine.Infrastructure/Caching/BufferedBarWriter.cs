using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Caching;

public sealed class BufferedBarWriter : IAsyncDisposable
{
    private long _droppedCount;
    private readonly Channel<(string RunId, Bar Bar)> _channel =
        Channel.CreateBounded<(string, Bar)>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumerTask;
    private readonly ILogger<BufferedBarWriter>? _logger;

    public BufferedBarWriter(IServiceScopeFactory scopeFactory, ILogger<BufferedBarWriter>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _consumerTask = ConsumeAsync(_cts.Token);
    }

    public bool Enqueue(string runId, Bar bar)
    {
        if (_channel.Writer.TryWrite((runId, bar)))
            return true;
        var dropped = Interlocked.Increment(ref _droppedCount);
        if (dropped % 1000 == 1)
            _logger?.LogWarning("BufferedBarWriter: channel full — dropped bar. Total drops: {Dropped}", dropped);
        return false;
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            var batch = new List<(string RunId, Bar Bar)>(500);
            await foreach (var item in _channel.Reader.ReadAllAsync(ct))
            {
                batch.Add(item);
                if (batch.Count >= 500)
                {
                    await FlushBatchAsync(batch, ct);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
                await FlushBatchAsync(batch, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "BufferedBarWriter consumer failed");
        }
    }

    private async Task FlushBatchAsync(List<(string RunId, Bar Bar)> batch, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBarRepository>();
        var groups = batch.GroupBy(x => x.RunId);
        foreach (var group in groups)
        {
            var bars = group.Select(x => x.Bar).ToList();
            await repo.BulkInsertAsync(group.Key, bars, ct);
        }
    }

    public async Task FlushAsync()
    {
        try { _channel.Writer.Complete(); } catch (ChannelClosedException) { }
        try { await _consumerTask; }
        catch (Exception ex) { _logger?.LogError(ex, "BufferedBarWriter flush failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync();
        _cts.Dispose();
    }
}
