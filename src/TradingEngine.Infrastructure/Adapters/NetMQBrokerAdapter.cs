using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using NetMQ;
using NetMQ.Sockets;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class NetMQBrokerAdapter : IBrokerAdapter, IAsyncDisposable
{
    private readonly string _dataEndpoint;
    private readonly string _commandEndpoint;
    private readonly ILogger<NetMQBrokerAdapter> _logger;

    private readonly Channel<Tick> _tickChannel = Channel.CreateBounded<Tick>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<Bar> _barChannel = Channel.CreateBounded<Bar>(new BoundedChannelOptions(2_000) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });
    private readonly Channel<AccountUpdate> _accountChannel = Channel.CreateBounded<AccountUpdate>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });
    private readonly Channel<ExecutionEvent> _execChannel = Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });

    public ChannelReader<Tick> TickStream => _tickChannel.Reader;
    public ChannelReader<Bar> BarStream => _barChannel.Reader;
    public ChannelReader<AccountUpdate> AccountStream => _accountChannel.Reader;
    public ChannelReader<ExecutionEvent> ExecutionStream => _execChannel.Reader;

    public Action? OnConnected { get; set; }
    public Action<string, string>? OnStatusChange { get; set; }

    public DateTime BrokerTimeUtc { get; private set; } = DateTime.UtcNow;
    public bool IsConnected => _cBotIdentity is not null;

    private SubscriberSocket? _sub;
    private RouterSocket? _router;
    private NetMQPoller? _poller;
    private NetMQQueue<(byte[] Identity, string Json)>? _sendQueue;
    private byte[]? _cBotIdentity;
    private readonly ConcurrentQueue<object> _pendingCommands = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
        { PropertyNameCaseInsensitive = true };

    public NetMQBrokerAdapter(string dataEndpoint, string commandEndpoint, ILogger<NetMQBrokerAdapter> logger)
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

        _logger.LogInformation("NETMQ|ADAPTER_STARTED|data={DataEndpoint}|command={CommandEndpoint}", _dataEndpoint, _commandEndpoint);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        _poller?.Stop();
        _poller?.Dispose();
        _sendQueue?.Dispose();
        _sub?.Dispose();
        _router?.Dispose();
        _tickChannel.Writer.TryComplete();
        _barChannel.Writer.TryComplete();
        _accountChannel.Writer.TryComplete();
        _execChannel.Writer.TryComplete();
        return Task.CompletedTask;
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
                _logger.LogInformation("NETMQ|SUB_RAW|topic={Topic}", topic);
                var frame = e.Socket.ReceiveFrameString();

                if (topic == "diag")
                {
                    _logger.LogInformation("CBOT|{Msg}", frame);
                    OnStatusChange?.Invoke("CBOT", frame);
                    continue;
                }

                using var doc = JsonDocument.Parse(frame);
                DispatchMessage(topic, doc.RootElement);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NETMQ|SUB_PARSE_ERR");
        }
    }

    private void DispatchMessage(string topic, JsonElement root)
    {
        switch (topic)
        {
            case "tick":
            {
                var tick = new Tick(
                    Symbol.Parse(root.GetProperty("symbol").GetString()!),
                    root.GetProperty("bid").GetDecimal(),
                    root.GetProperty("ask").GetDecimal(),
                    root.GetProperty("time").GetDateTime().ToUniversalTime());
                BrokerTimeUtc = tick.TimestampUtc;
                _tickChannel.Writer.TryWrite(tick);
                break;
            }
            case "bar":
            {
                var bar = new Bar(
                    Symbol.Parse(root.GetProperty("symbol").GetString()!),
                    Enum.Parse<Timeframe>(root.GetProperty("period").GetString()!, ignoreCase: true),
                    root.GetProperty("openTime").GetDateTime().ToUniversalTime(),
                    root.GetProperty("open").GetDecimal(),
                    root.GetProperty("high").GetDecimal(),
                    root.GetProperty("low").GetDecimal(),
                    root.GetProperty("close").GetDecimal(),
                    root.GetProperty("volume").GetDouble());
                _barChannel.Writer.TryWrite(bar);
                break;
            }
            case "acct":
            {
                var acct = new AccountUpdate(
                    root.GetProperty("balance").GetDecimal(),
                    root.GetProperty("equity").GetDecimal(),
                    root.GetProperty("floatingPnL").GetDecimal(),
                    root.GetProperty("time").GetDateTime().ToUniversalTime());
                _accountChannel.Writer.TryWrite(acct);
                break;
            }
            case "exec":
            {
                var orderId = root.GetProperty("clientOrderId").GetGuid();
                var state = Enum.Parse<OrderState>(root.GetProperty("state").GetString()!, ignoreCase: true);
                var fillPrice = root.GetProperty("fillPrice").GetDecimal();
                var filledLots = root.GetProperty("filledLots").GetDecimal();
                var reason = root.GetProperty("reason").ValueKind == JsonValueKind.String
                    ? root.GetProperty("reason").GetString() : null;
                var time = root.GetProperty("time").GetDateTime().ToUniversalTime();
                var exec = new ExecutionEvent(orderId, state,
                    fillPrice > 0 ? new Price(fillPrice) : null,
                    filledLots, reason, time);
                _execChannel.Writer.TryWrite(exec);
                break;
            }
        }
    }

    private void OnRouterReceive(object? sender, NetMQSocketEventArgs e)
    {
        while (e.Socket.TryReceiveFrameBytes(out var identity))
        {
            var json = e.Socket.ReceiveFrameString();
            if (_cBotIdentity is null)
            {
                _cBotIdentity = identity;
                _logger.LogInformation("NETMQ|CONNECTED|dataEndpoint={DataEndpoint}|commandEndpoint={CommandEndpoint}", _dataEndpoint, _commandEndpoint);
                OnStatusChange?.Invoke("NETMQ_CONNECTED", $"cBot connected via ROUTER");
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "hello")
                {
                    var helloAck = """{"type":"hello_ack","v":1}""";
                    _sendQueue!.Enqueue((identity, helloAck));
                    _logger.LogInformation("NETMQ|HELLO_ACK_SENT|identity captured, handshake complete");
                    FlushPendingCommands();
                    OnConnected?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NETMQ|ROUTER_PARSE_ERR");
            }
        }
    }

    private void FlushPendingCommands()
    {
        if (_cBotIdentity is null) return;
        while (_pendingCommands.TryDequeue(out var cmd))
        {
            var json = JsonSerializer.Serialize(cmd, cmd.GetType(), JsonOpts);
            _sendQueue!.Enqueue((_cBotIdentity!, json));
            _logger.LogInformation("NETMQ|FLUSH_PENDING|type={Type}", cmd.GetType().Name);
        }
    }

    public async Task<Guid> SubmitOrderAsync(OrderRequest request, CancellationToken ct)
    {
        var clientOrderId = Guid.NewGuid();
        var connected = _cBotIdentity is not null;
        _logger.LogInformation("NETMQ|SUBMIT_ORDER|id={Id}|symbol={Symbol}|dir={Dir}|lots={Lots}|connected={Connected}",
            clientOrderId, request.Symbol, request.Direction, request.Lots, connected);
        if (!connected)
        {
            var cmd = new
            {
                type = "submit_order",
                clientOrderId = clientOrderId.ToString(),
                symbol = request.Symbol.Value,
                direction = request.Direction.ToString(),
                lots = (double)request.Lots,
                slPrice = (double)request.Intent.StopLoss.Value,
                tpPrice = request.Intent.TakeProfit.HasValue ? (double)request.Intent.TakeProfit.Value.Value : 0.0
            };
            _pendingCommands.Enqueue(cmd);
            OnStatusChange?.Invoke("NETMQ_QUEUED", $"ORDER QUEUED (cBot not yet connected): {request.Direction} {request.Lots:F2} {request.Symbol.Value}");
            return clientOrderId;
        }
        OnStatusChange?.Invoke("NETMQ_SENT", $"ORDER SENT to cBot: {request.Direction} {request.Lots:F2} {request.Symbol.Value}");
        await SendCommandAsync(new
        {
            type = "submit_order",
            clientOrderId = clientOrderId.ToString(),
            symbol = request.Symbol.Value,
            direction = request.Direction.ToString(),
            lots = (double)request.Lots,
            slPrice = (double)request.Intent.StopLoss.Value,
            tpPrice = request.Intent.TakeProfit.HasValue ? (double)request.Intent.TakeProfit.Value.Value : 0.0
        }, ct);
        return clientOrderId;
    }

    public Task ModifyOrderAsync(Guid orderId, Price newSl, Price? newTp, CancellationToken ct)
        => SendCommandAsync(new { type = "modify_order", orderId = orderId.ToString(), newSl = (double)newSl.Value, newTp = newTp.HasValue ? (double)newTp.Value.Value : 0.0 }, ct);

    public Task CancelOrderAsync(Guid orderId, CancellationToken ct)
        => SendCommandAsync(new { type = "cancel_order", orderId = orderId.ToString() }, ct);

    public Task ClosePositionAsync(Guid positionId, CancellationToken ct)
    {
        if (_router is null)
        {
            _execChannel.Writer.TryWrite(
                new ExecutionEvent(positionId, OrderState.Filled,
                    new Price(1m), 0, "FORCE_CLOSE_ENGINE_SHUTDOWN", BrokerTimeUtc));
            return Task.CompletedTask;
        }
        return SendCommandAsync(new { type = "close_position", positionId = positionId.ToString() }, ct);
    }

    public Task SendShutdownAsync(CancellationToken ct)
        => SendCommandAsync(new { type = "shutdown" }, ct);

    private Task SendCommandAsync(object command, CancellationToken ct)
    {
        if (_router is null)
        {
            _logger.LogWarning("NETMQ|CMD_DROPPED|reason=router null (disposed)");
            return Task.CompletedTask;
        }
        if (_cBotIdentity is null)
        {
            _pendingCommands.Enqueue(command);
            _logger.LogDebug("NETMQ|CMD_QUEUED|reason=identity not yet known");
            return Task.CompletedTask;
        }
        var json = JsonSerializer.Serialize(command, command.GetType(), JsonOpts);
        _sendQueue!.Enqueue((_cBotIdentity!, json));
        return Task.CompletedTask;
    }

    public Task<AccountState> GetAccountStateAsync(CancellationToken ct)
        => Task.FromResult(new AccountState(0, 0, []));

    public async ValueTask DisposeAsync() => await DisconnectAsync(CancellationToken.None);
}
