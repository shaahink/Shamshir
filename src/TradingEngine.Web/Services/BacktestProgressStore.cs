using System.Collections.Concurrent;
using System.Threading.Channels;

namespace TradingEngine.Web.Services;

public sealed class BacktestProgressStore
{
    private readonly ConcurrentDictionary<string, Channel<string>> _channels = new();

    public ChannelWriter<string> GetWriter(string runId)
    {
        var ch = _channels.GetOrAdd(runId, _ =>
            Channel.CreateBounded<string>(new BoundedChannelOptions(500)
            { FullMode = BoundedChannelFullMode.DropOldest }));
        return ch.Writer;
    }

    public ChannelReader<string>? GetReader(string runId) =>
        _channels.TryGetValue(runId, out var ch) ? ch.Reader : null;

    public void Complete(string runId)
    {
        if (_channels.TryRemove(runId, out var ch))
            ch.Writer.TryComplete();
    }
}
