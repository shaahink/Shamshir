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
    private readonly IPipelineJournal? _journal;

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
    private readonly List<object> _bufferedCommands = new();
    private readonly object _bufferLock = new();
    public long CurrentBarSeq { get; private set; }

    private long _barsReceived;
    private long _commandsSent;
    private long _execsReceived;

    private static readonly JsonSerializerOptions JsonOpts = new()
        { PropertyNameCaseInsensitive = true };

    public NetMQBrokerAdapter(string dataEndpoint, string commandEndpoint,
        ILogger<NetMQBrokerAdapter> logger, IPipelineJournal? journal = null)
    {
        _dataEndpoint = dataEndpoint;
        _commandEndpoint = commandEndpoint;
        _logger = logger;
        _journal = journal;
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
                if (!e.Socket.TryReceiveFrameString(out var frame))
                    break;

                if (topic == "diag")
                {
                    _logger.LogInformation("CBOT|{Msg}", frame);
                    OnStatusChange?.Invoke("CBOT", frame);
                    continue;
                }

                if (topic.StartsWith('{'))
                    continue;

                using var doc = JsonDocument.Parse(frame);
                DispatchSubMessage(topic, doc.RootElement);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NETMQ|SUB_PARSE_ERR");
        }
    }

    private void DispatchSubMessage(string topic, JsonElement root)
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

                switch (type)
                {
                    case "hello":
                        var helloAck = """{"type":"hello_ack","v":1}""";
                        _sendQueue!.Enqueue((identity, helloAck));
                        _logger.LogInformation("NETMQ|HELLO_ACK_SENT|identity captured, handshake complete");
                        FlushPendingCommands();
                        OnConnected?.Invoke();
                        _journal?.Write("CONNECTED", null, DateTime.UtcNow);
                        break;

                    case "bar":
                        CurrentBarSeq = doc.RootElement.TryGetProperty("seq", out var s) ? s.GetInt64() : 0;
                        _barsReceived++;
                        var bar = new Bar(
                            Symbol.Parse(doc.RootElement.GetProperty("symbol").GetString()!),
                            Enum.Parse<Timeframe>(doc.RootElement.GetProperty("period").GetString()!, ignoreCase: true),
                            doc.RootElement.GetProperty("openTime").GetDateTime().ToUniversalTime(),
                            doc.RootElement.GetProperty("open").GetDecimal(),
                            doc.RootElement.GetProperty("high").GetDecimal(),
                            doc.RootElement.GetProperty("low").GetDecimal(),
                            doc.RootElement.GetProperty("close").GetDecimal(),
                            doc.RootElement.GetProperty("volume").GetDouble());
                        BrokerTimeUtc = bar.OpenTimeUtc;
                        _barChannel.Writer.TryWrite(bar);
                        _journal?.Write("BAR_RECV", null, bar.OpenTimeUtc,
                            JsonSerializer.Serialize(new { bar.Symbol.Value, bar.Close }, JsonOpts));

                        if (doc.RootElement.TryGetProperty("account", out var barAcct))
                        {
                            var acctUpdate = new AccountUpdate(
                                barAcct.GetProperty("balance").GetDecimal(),
                                barAcct.GetProperty("equity").GetDecimal(),
                                barAcct.GetProperty("equity").GetDecimal() - barAcct.GetProperty("balance").GetDecimal(),
                                bar.OpenTimeUtc);
                            _accountChannel.Writer.TryWrite(acctUpdate);
                        }
                        break;

                    case "bar_result":
                        if (doc.RootElement.TryGetProperty("execs", out var execs) && execs.ValueKind == JsonValueKind.Array)
                        {
                            var count = 0;
                            foreach (var ex in execs.EnumerateArray())
                            {
                                count++;
                                var orderId = ex.TryGetProperty("clientOrderId", out var oid) && oid.GetString() is { } oidStr
                                    ? Guid.Parse(oidStr) : Guid.Empty;
                                var kind = ex.TryGetProperty("kind", out var k) ? k.GetString() : null;
                                var state = Enum.Parse<OrderState>(ex.GetProperty("state").GetString()!, ignoreCase: true);
                                var fillPrice = ex.GetProperty("fillPrice").GetDecimal();
                                var filledLots = ex.GetProperty("filledLots").GetDecimal();
                                var reason = ex.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                                    ? r.GetString() : null;
                                var time = ex.TryGetProperty("simTime", out var st) ? st.GetDateTime().ToUniversalTime() : DateTime.UtcNow;
                                var exec = new ExecutionEvent(orderId, state,
                                    fillPrice > 0 ? new Price(fillPrice) : null,
                                    filledLots, reason, time)
                                {
                                    GrossProfit = ParseDecimalOrNull(ex, "grossProfit"),
                                    NetProfit = ParseDecimalOrNull(ex, "netProfit"),
                                    Commission = ParseDecimalOrNull(ex, "commission"),
                                    Swap = ParseDecimalOrNull(ex, "swap"),
                                };
                                _execChannel.Writer.TryWrite(exec);
                            }
                            _execsReceived += count;
                            _journal?.Write("EXEC_RECV", null, DateTime.UtcNow,
                                JsonSerializer.Serialize(new { count }, JsonOpts));
                        }
                        if (doc.RootElement.TryGetProperty("account", out var acct))
                        {
                            var acctUpdate = new AccountUpdate(
                                acct.GetProperty("balance").GetDecimal(),
                                acct.GetProperty("equity").GetDecimal(),
                                acct.GetProperty("equity").GetDecimal() - acct.GetProperty("balance").GetDecimal(),
                                DateTime.UtcNow);
                            _accountChannel.Writer.TryWrite(acctUpdate);
                        }
                        break;

                    case "exec":
                    {
                        var orderId = doc.RootElement.TryGetProperty("clientOrderId", out var o) && o.GetString() is { } oStr
                            ? Guid.Parse(oStr) : Guid.Empty;
                        var state = Enum.Parse<OrderState>(doc.RootElement.GetProperty("state").GetString()!, ignoreCase: true);
                        var fillPrice = doc.RootElement.GetProperty("fillPrice").GetDecimal();
                        var filledLots = doc.RootElement.GetProperty("filledLots").GetDecimal();
                        var reason = doc.RootElement.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String
                            ? re.GetString() : null;
                        var time = doc.RootElement.TryGetProperty("simTime", out var st) ? st.GetDateTime().ToUniversalTime() : DateTime.UtcNow;
                        var execEvt = new ExecutionEvent(orderId, state,
                            fillPrice > 0 ? new Price(fillPrice) : null,
                            filledLots, reason, time)
                        {
                            GrossProfit = ParseDecimalOrNull(doc.RootElement, "grossProfit"),
                            NetProfit = ParseDecimalOrNull(doc.RootElement, "netProfit"),
                            Commission = ParseDecimalOrNull(doc.RootElement, "commission"),
                            Swap = ParseDecimalOrNull(doc.RootElement, "swap"),
                        };
                        _execChannel.Writer.TryWrite(execEvt);
                        _execsReceived++;
                        break;
                    }

                    case "stats":
                        _logger.LogInformation("NETMQ|STATS|{Json}", json);
                        _journal?.Write("STATS", null, DateTime.UtcNow);
                        try
                        {
                            var cBarsSent = doc.RootElement.TryGetProperty("barsSent", out var bs) ? bs.GetInt64() : 0;
                            var cCmdsRecv = doc.RootElement.TryGetProperty("cmdsReceived", out var cr) ? cr.GetInt64() : 0;
                            var cExecsSent = doc.RootElement.TryGetProperty("execsSent", out var es) ? es.GetInt64() : 0;

                            var barsOk = cBarsSent == _barsReceived ? "✓" : "✗";
                            var cmdsOk = cCmdsRecv == _commandsSent ? "✓" : "✗";
                            var execsOk = cExecsSent == _execsReceived ? "✓" : "✗";

                            var recLine = $"RECONCILE bars: sent={cBarsSent} recv={_barsReceived} {barsOk} | cmds: sent={_commandsSent} recv={cCmdsRecv} {cmdsOk} | execs: sent={cExecsSent} recv={_execsReceived} {execsOk}";
                            _logger.LogInformation("NETMQ|RECONCILE|{Result}", recLine);
                            OnStatusChange?.Invoke("RECONCILE", recLine);
                        }
                        catch { }
                        break;
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
        if (!connected)
        {
            _pendingCommands.Enqueue(cmd);
            OnStatusChange?.Invoke("NETMQ_QUEUED", $"ORDER QUEUED (cBot not yet connected): {request.Direction} {request.Lots:F2} {request.Symbol.Value}");
            return clientOrderId;
        }
        lock (_bufferLock) { _bufferedCommands.Add(cmd); }
        OnStatusChange?.Invoke("NETMQ_BUFFERED", $"ORDER BUFFERED for bar: {request.Direction} {request.Lots:F2} {request.Symbol.Value}");
        return clientOrderId;
    }

    public Task ModifyOrderAsync(Guid orderId, Price newSl, Price? newTp, CancellationToken ct)
    {
        var cmd = new { type = "modify_order", orderId = orderId.ToString(), newSl = (double)newSl.Value, newTp = newTp.HasValue ? (double)newTp.Value.Value : 0.0 };
        lock (_bufferLock) { _bufferedCommands.Add(cmd); }
        return Task.CompletedTask;
    }

    public Task CancelOrderAsync(Guid orderId, CancellationToken ct)
    {
        var cmd = new { type = "cancel_order", orderId = orderId.ToString() };
        lock (_bufferLock) { _bufferedCommands.Add(cmd); }
        return Task.CompletedTask;
    }

    public Task ClosePositionAsync(Guid positionId, CancellationToken ct)
    {
        if (_router is null)
        {
            _execChannel.Writer.TryWrite(
                new ExecutionEvent(positionId, OrderState.Filled,
                    new Price(1m), 0, "FORCE_CLOSE_ENGINE_SHUTDOWN", BrokerTimeUtc));
            return Task.CompletedTask;
        }
        var cmd = new { type = "close_position", positionId = positionId.ToString() };
        lock (_bufferLock) { _bufferedCommands.Add(cmd); }
        return Task.CompletedTask;
    }

    public Task SendShutdownAsync(CancellationToken ct)
        => SendCommandAsync(new { type = "shutdown" }, ct);

    public async Task CompleteBarAsync(long seq, CancellationToken ct)
    {
        object[] commands;
        lock (_bufferLock)
        {
            commands = _bufferedCommands.ToArray();
            _bufferedCommands.Clear();
        }

        var barDone = new
        {
            type = "bar_done",
            v = 1,
            seq = seq,
            commands = commands
        };
        var json = JsonSerializer.Serialize(barDone, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        if (_cBotIdentity is not null && _sendQueue is not null)
        {
            _sendQueue.Enqueue((_cBotIdentity!, json));
            _commandsSent += commands.Length;
            _journal?.Write("ORDER_SENT", null, DateTime.UtcNow,
                JsonSerializer.Serialize(new { seq, count = commands.Length }, JsonOpts));
            _logger.LogInformation("NETMQ|BAR_DONE|seq={Seq}|commands={Count}", seq, commands.Length);
            OnStatusChange?.Invoke("BAR_DONE", $"BAR_DONE seq={seq} commands={commands.Length}");
        }
        else
        {
            _logger.LogWarning("NETMQ|BAR_DONE_FAILED|seq={Seq}|reason=cBot not connected", seq);
        }

        await Task.CompletedTask;
    }

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

    private static decimal? ParseDecimalOrNull(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetDecimal();
        return null;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync(CancellationToken.None);
}
