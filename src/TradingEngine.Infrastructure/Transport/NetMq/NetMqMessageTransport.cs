using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NetMQ;
using NetMQ.Sockets;
using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.Transport.NetMq;

public sealed class NetMqMessageTransport : IMessageTransport, ITransportStatusSource, IAsyncDisposable
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

    // P0.2 (F5): teardown must be idempotent. The orchestrator disconnects twice on a cTrader run —
    // once via the BarStream safety-net force-disconnect, then again via host disposal
    // (adapter.DisposeAsync → transport.DisconnectAsync). NetMQPoller.Stop()/Dispose() throw
    // ObjectDisposedException("NetMQPoller") on a second call, and that exception used to propagate to
    // the orchestrator's outer catch and stamp a COMPLETE run as `failed` (the audited F5 crash). A
    // single-shot guard makes the second teardown a no-op so a completed run stays completed.
    private int _teardownStarted;

    private TransportPhase _phase = TransportPhase.Disconnected;
    private DateTime? _connectedAtUtc;
    private DateTime? _disconnectedAtUtc;
    private DateTime _lastMessageAtUtc;
    private int _barsReceived;
    private int _commandsSent;
    private int _executionsReceived;
    private string? _lastError;

    public TransportStatus Current => new(
        _phase, _connectedAtUtc, _disconnectedAtUtc,
        _lastMessageAtUtc, Volatile.Read(ref _barsReceived),
        Volatile.Read(ref _commandsSent), Volatile.Read(ref _executionsReceived),
        _lastError);

    public event Action<TransportStatus>? StatusChanged;

    public NetMqMessageTransport(string dataEndpoint, string commandEndpoint,
        ILogger<NetMqMessageTransport> logger)
    {
        _dataEndpoint = dataEndpoint;
        _commandEndpoint = commandEndpoint;
        _logger = logger;
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        TransitionTo(TransportPhase.Connecting);

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
        // P0.2 (F5): idempotent — only the first caller tears down the poller/sockets. A second call
        // (host disposal after the safety-net force-disconnect) returns immediately instead of calling
        // NetMQPoller.Stop() on an already-disposed poller, which threw the F5 "disposed NetMQPoller".
        if (Interlocked.Exchange(ref _teardownStarted, 1) == 1)
            return;

        TransitionTo(TransportPhase.Disconnected);
        _disconnectedAtUtc = DateTime.UtcNow;

        if (_sub != null) _sub.ReceiveReady -= OnSubReceive;
        if (_router != null) _router.ReceiveReady -= OnRouterReceive;
        if (_sendQueue != null) _sendQueue.ReceiveReady -= OnSendQueueReady;

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
        Interlocked.Increment(ref _commandsSent);
    }

    private void OnSendQueueReady(object? sender, NetMQQueueEventArgs<(byte[] Identity, string Json)> e)
    {
        while (e.Queue.TryDequeue(out var item, TimeSpan.Zero))
            _router!.SendMoreFrame(item.Identity).SendFrame(item.Json);
    }

    public void AcknowledgeHandshake()
    {
        if (_phase == TransportPhase.HandshakeReceived)
            TransitionTo(TransportPhase.HandshakeAcknowledged);
    }

    private void TransitionTo(TransportPhase phase)
    {
        _phase = phase;
        try { StatusChanged?.Invoke(Current); }
        catch { }
    }

    private void TransportToError()
    {
        _phase = TransportPhase.Error;
        try { StatusChanged?.Invoke(Current); }
        catch { }
    }

    private void OnSubReceive(object? sender, NetMQSocketEventArgs e)
    {
        try
        {
            while (e.Socket.TryReceiveFrameString(out var topic))
            {
                if (!e.Socket.TryReceiveFrameString(out var frame))
                    break;

                _lastMessageAtUtc = DateTime.UtcNow;

                if (topic == "diag")
                {
                    _logger.LogInformation("CBOT|{Msg}", frame);
                    continue;
                }

                if (topic.StartsWith('{'))
                    continue;

                if (topic == "acct")
                    _phase = TransportPhase.Connected;

                _subChannel.Writer.TryWrite((topic, frame));
                Interlocked.Increment(ref _barsReceived);
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            TransportToError();
            _logger.LogWarning(ex, "NETMQ|SUB_PARSE_ERR");
        }
    }

    private void OnRouterReceive(object? sender, NetMQSocketEventArgs e)
    {
        while (e.Socket.TryReceiveFrameBytes(out var identity))
        {
            _lastMessageAtUtc = DateTime.UtcNow;

            var json = e.Socket.ReceiveFrameString();
            var isNewConnection = _cBotIdentity is null;
            _cBotIdentity = identity;

            if (isNewConnection)
            {
                _connectedAtUtc = DateTime.UtcNow;
                TransitionTo(TransportPhase.HandshakeReceived);
                _logger.LogInformation("NETMQ|CONNECTED|data={DataEndpoint}|command={CommandEndpoint}", _dataEndpoint, _commandEndpoint);
            }

            _routerChannel.Writer.TryWrite((identity, json));
            Interlocked.Increment(ref _executionsReceived);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None);
    }
}
