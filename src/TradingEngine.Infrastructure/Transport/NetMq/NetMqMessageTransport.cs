using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NetMQ;
using NetMQ.Sockets;

namespace TradingEngine.Infrastructure.Transport.NetMq;

public sealed class NetMqMessageTransport : IMessageTransport, IAsyncDisposable
{
    private readonly string _dataEndpoint;
    private readonly string _commandEndpoint;
    private readonly ILogger<NetMqMessageTransport> _logger;

    private readonly Channel<(string Topic, string Json)> _subChannel =
        Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<(byte[] Identity, string Json)> _routerChannel =
        Channel.CreateBounded<(byte[], string)>(new BoundedChannelOptions(2_000) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });

    public ChannelReader<(string Topic, string Json)> SubMessages => _subChannel.Reader;
    public ChannelReader<(byte[] Identity, string Json)> RouterMessages => _routerChannel.Reader;

    public bool IsConnected => _cBotIdentity is not null;
    public Action? OnConnected { get; set; }

    private SubscriberSocket? _sub;
    private RouterSocket? _router;
    private NetMQPoller? _poller;
    private NetMQQueue<(byte[] Identity, string Json)>? _sendQueue;
    private byte[]? _cBotIdentity;

    public NetMqMessageTransport(string dataEndpoint, string commandEndpoint,
        ILogger<NetMqMessageTransport> logger)
    {
        _dataEndpoint = dataEndpoint;
        _commandEndpoint = commandEndpoint;
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        _sub = new SubscriberSocket();
        _sub.Connect(_dataEndpoint);
        _sub.SubscribeToAnyTopic();
        _sub.ReceiveReady += OnSubReceive;

        _router = new RouterSocket();
        _router.Bind(_commandEndpoint);
        _router.ReceiveReady += OnRouterReceive;

        _sendQueue = new NetMQQueue<(byte[], string)>();
        _sendQueue.ReceiveReady += OnSendQueueReady;

        _poller = new NetMQPoller { _sub, _router, _sendQueue };
        _poller.RunAsync();

        _logger.LogInformation("NETMQ|TRANSPORT_STARTED|data={DataEndpoint}|command={CommandEndpoint}", _dataEndpoint, _commandEndpoint);
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        _poller?.Stop();
        _poller?.Dispose();
        _sendQueue?.Dispose();
        _sub?.Dispose();
        _router?.Dispose();

        await Task.Delay(200, ct);
        _subChannel.Writer.TryComplete();
        _routerChannel.Writer.TryComplete();
    }

    public void Send(byte[] identity, string json)
    {
        _sendQueue?.Enqueue((identity, json));
    }

    private void OnSendQueueReady(object? sender, NetMQQueueEventArgs<(byte[] Identity, string Json)> e)
    {
        while (e.Queue.TryDequeue(out var item, TimeSpan.Zero))
            _router!.SendMoreFrame(item.Identity).SendFrame(item.Json);
    }

    private void OnSubReceive(object? sender, NetMQSocketEventArgs e)
    {
        try
        {
            while (e.Socket.TryReceiveFrameString(out var topic))
            {
                if (!e.Socket.TryReceiveFrameString(out var frame))
                    break;

                if (topic == "diag")
                {
                    _logger.LogInformation("CBOT|{Msg}", frame);
                    continue;
                }

                if (topic.StartsWith('{'))
                    continue;

                _subChannel.Writer.TryWrite((topic, frame));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NETMQ|SUB_PARSE_ERR");
        }
    }

    private void OnRouterReceive(object? sender, NetMQSocketEventArgs e)
    {
        while (e.Socket.TryReceiveFrameBytes(out var identity))
        {
            var json = e.Socket.ReceiveFrameString();
            var isNewConnection = _cBotIdentity is null;
            _cBotIdentity = identity;

            if (isNewConnection)
            {
                _logger.LogInformation("NETMQ|CONNECTED|data={DataEndpoint}|command={CommandEndpoint}", _dataEndpoint, _commandEndpoint);
            }

            _routerChannel.Writer.TryWrite((identity, json));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None);
    }
}
