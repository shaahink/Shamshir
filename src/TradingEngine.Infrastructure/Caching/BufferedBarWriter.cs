using System.Threading.Channels;

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

    public BufferedBarWriter(IBarRepository repo)
    {
        _repo = repo;
        _consumerTask = ConsumeAsync(_cts.Token);
    }

    public bool Enqueue(Bar bar) => _channel.Writer.TryWrite(bar);

    public async Task ConsumeAsync(CancellationToken ct)
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

    public async Task FlushAsync()
    {
        _channel.Writer.Complete();
        await _consumerTask;
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync();
        _cts.Dispose();
    }
}
