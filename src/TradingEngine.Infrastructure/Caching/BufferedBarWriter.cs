using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Caching;

public sealed class BufferedBarWriter : IAsyncDisposable
{
    private readonly Channel<Bar> _channel =
        Channel.CreateBounded<Bar>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });

    private readonly IBarRepository _repo;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumerTask;
    private readonly ILogger<BufferedBarWriter>? _logger;

    public BufferedBarWriter(IBarRepository repo, ILogger<BufferedBarWriter>? logger = null)
    {
        _repo = repo;
        _logger = logger;
        _consumerTask = ConsumeAsync(_cts.Token);
    }

    public bool Enqueue(Bar bar) => _channel.Writer.TryWrite(bar);

    public async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            var batch = new List<Bar>(500);
            await foreach (var bar in _channel.Reader.ReadAllAsync(ct))
            {
                batch.Add(bar);
                if (batch.Count >= 500)
                {
                    await _repo.BulkInsertAsync(batch, ct);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
                await _repo.BulkInsertAsync(batch, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "BufferedBarWriter consumer failed");
        }
    }

    public async Task FlushAsync()
    {
        _channel.Writer.Complete();
        try { await _consumerTask; }
        catch (Exception ex) { _logger?.LogError(ex, "BufferedBarWriter flush failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync();
        _cts.Dispose();
    }
}
