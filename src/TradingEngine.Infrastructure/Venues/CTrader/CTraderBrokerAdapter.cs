using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Venues.CTrader;

public sealed class CTraderBrokerAdapter : IBrokerAdapter, IAsyncDisposable
{
    private readonly IMessageTransport _transport;
    private readonly ILogger<CTraderBrokerAdapter> _logger;
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
    public bool IsConnected => _transport.IsConnected;

    private readonly ConcurrentQueue<object> _pendingCommands = new();
    private readonly List<object> _bufferedCommands = new();
    private readonly object _bufferLock = new();
    private byte[]? _cBotIdentity;
    public long CurrentBarSeq { get; private set; }

    private long _barsReceived;
    private long _commandsSent;
    private long _execsReceived;
    private long _execsDeduped;
    private readonly HashSet<string> _recentExecSigs = new();
    private readonly Queue<string> _recentExecOrder = new();
    private const int MaxRecentExecSigs = 500;
    private readonly CancellationTokenSource _cts = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
        { PropertyNameCaseInsensitive = true };

    public CTraderBrokerAdapter(IMessageTransport transport,
        ILogger<CTraderBrokerAdapter> logger, IPipelineJournal? journal = null)
    {
        _transport = transport;
        _logger = logger;
        _journal = journal;
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        _transport.OnConnected = OnTransportConnected;
        var connectTask = _transport.ConnectAsync(ct);

        _ = Task.Run(() => ReadSubLoop(_cts.Token), _cts.Token);
        _ = Task.Run(() => ReadRouterLoop(_cts.Token), _cts.Token);

        return connectTask;
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        _cts.Cancel();
        await _transport.DisconnectAsync(ct);
        await Task.Delay(200, ct);
        _tickChannel.Writer.TryComplete();
        _barChannel.Writer.TryComplete();
        _accountChannel.Writer.TryComplete();
        _execChannel.Writer.TryComplete();
    }

    private void OnTransportConnected()
    {
        _logger.LogInformation("CTRADER|TRANSPORT_CONNECTED");
        FlushPendingCommands();
        OnConnected?.Invoke();
    }

    private async Task ReadSubLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var (topic, json) in _transport.SubMessages.ReadAllAsync(ct))
            {
                using var doc = JsonDocument.Parse(json);
                DispatchSubMessage(topic, doc.RootElement);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CTRADER|SUB_LOOP_ERR");
        }
    }

    private async Task ReadRouterLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var (identity, json) in _transport.RouterMessages.ReadAllAsync(ct))
            {
                _cBotIdentity = identity;

                if (IsConnected)
                {
                    OnStatusChange?.Invoke("NETMQ_CONNECTED", "cBot connected via ROUTER");
                }

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

                    switch (type)
                    {
                        case "hello":
                            var helloAck = """{"type":"hello_ack","v":1}""";
                            _transport.Send(identity, helloAck);
                            _logger.LogInformation("CTRADER|HELLO_ACK_SENT|handshake complete");
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
                            HandleBarResult(doc.RootElement);
                            break;

                        case "exec":
                            HandleExecEvent(doc.RootElement);
                            break;

                        case "stats":
                            _logger.LogInformation("CTRADER|STATS|{Json}", json);
                            _journal?.Write("STATS", null, DateTime.UtcNow);
                            HandleStats(doc.RootElement);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CTRADER|ROUTER_PARSE_ERR");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CTRADER|ROUTER_LOOP_ERR");
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

    private void HandleBarResult(JsonElement root)
    {
        if (root.TryGetProperty("execs", out var execs) && execs.ValueKind == JsonValueKind.Array)
        {
            var count = 0;
            foreach (var ex in execs.EnumerateArray())
            {
                count++;
                var exec = ParseExecution(ex);
                TryWriteExec(exec);
            }
            _execsReceived += count;
            _journal?.Write("EXEC_RECV", null, DateTime.UtcNow,
                JsonSerializer.Serialize(new { count }, JsonOpts));
        }
        if (root.TryGetProperty("account", out var acct))
        {
            var acctUpdate = new AccountUpdate(
                acct.GetProperty("balance").GetDecimal(),
                acct.GetProperty("equity").GetDecimal(),
                acct.GetProperty("equity").GetDecimal() - acct.GetProperty("balance").GetDecimal(),
                DateTime.UtcNow);
            _accountChannel.Writer.TryWrite(acctUpdate);
        }
    }

    private void HandleExecEvent(JsonElement root)
    {
        var exec = ParseExecution(root);
        TryWriteExec(exec);
        _execsReceived++;
    }

    private void HandleStats(JsonElement root)
    {
        try
        {
            var cBarsSent = root.TryGetProperty("barsSent", out var bs) ? bs.GetInt64() : 0;
            var cCmdsRecv = root.TryGetProperty("cmdsReceived", out var cr) ? cr.GetInt64() : 0;
            var cExecsSent = root.TryGetProperty("execsSent", out var es) ? es.GetInt64() : 0;

            var barsOk = cBarsSent == _barsReceived ? "v" : "x";
            var cmdsOk = cCmdsRecv == _commandsSent ? "v" : "x";
            var uniqueExecs = _execsReceived - _execsDeduped;
            var execsOk = cExecsSent == uniqueExecs ? "v" : "x";

            var recLine = $"RECONCILE bars: sent={cBarsSent} recv={_barsReceived} {barsOk} | cmds: sent={_commandsSent} recv={cCmdsRecv} {cmdsOk} | execs: sent={cExecsSent} recv={_execsReceived} dedup={_execsDeduped} unique={uniqueExecs} {execsOk}";
            _logger.LogInformation("CTRADER|RECONCILE|{Result}", recLine);
            OnStatusChange?.Invoke("RECONCILE", recLine);
        }
        catch { }
    }

    private static ExecutionEvent ParseExecution(JsonElement ex)
    {
        var orderId = ex.TryGetProperty("clientOrderId", out var oid) && oid.GetString() is { } oidStr
            ? Guid.Parse(oidStr) : Guid.Empty;
        var state = Enum.Parse<OrderState>(ex.GetProperty("state").GetString()!, ignoreCase: true);
        var fillPrice = ex.GetProperty("fillPrice").GetDecimal();
        var filledLots = ex.GetProperty("filledLots").GetDecimal();
        var reason = ex.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
        var time = ex.TryGetProperty("simTime", out var st) ? st.GetDateTime().ToUniversalTime() : DateTime.UtcNow;
        return new ExecutionEvent(orderId, state,
            fillPrice > 0 ? new Price(fillPrice) : null,
            filledLots, reason, time)
        {
            GrossProfit = ParseDecimalOrNull(ex, "grossProfit"),
            NetProfit = ParseDecimalOrNull(ex, "netProfit"),
            Commission = ParseDecimalOrNull(ex, "commission"),
            Swap = ParseDecimalOrNull(ex, "swap"),
        };
    }

    private void FlushPendingCommands()
    {
        if (!IsConnected) return;
        while (_pendingCommands.TryDequeue(out var cmd))
        {
            var json = JsonSerializer.Serialize(cmd, cmd.GetType(), JsonOpts);
            _transport.Send(_cBotIdentity!, json);
            _logger.LogInformation("CTRADER|FLUSH_PENDING|type={Type}", cmd.GetType().Name);
        }
    }

    public Task<Guid> SubmitOrderAsync(OrderRequest request, CancellationToken ct)
    {
        var clientOrderId = Guid.NewGuid();
        var connected = IsConnected;
        _logger.LogInformation("CTRADER|SUBMIT_ORDER|id={Id}|symbol={Symbol}|dir={Dir}|lots={Lots}|connected={Connected}",
            clientOrderId, request.Symbol, request.Direction, request.Lots, connected);
        var entryOpts = request.Intent.Entry;
        var isLimit = entryOpts?.Method == OrderEntryMethod.LimitOffset;
        var cmd = new
        {
            type = "submit_order",
            clientOrderId = clientOrderId.ToString(),
            symbol = request.Symbol.Value,
            direction = request.Direction.ToString(),
            lots = (double)request.Lots,
            orderType = isLimit ? "Limit" : "Market",
            limitPrice = isLimit ? (double)request.Intent.LimitPrice!.Value.Value : 0.0,
            expiryBars = isLimit ? (entryOpts!.LimitOrderExpiryBars) : 0,
            maxSlippagePips = entryOpts?.MaxSlippagePips ?? 2.0,
            slPrice = (double)request.Intent.StopLoss.Value,
            tpPrice = request.Intent.TakeProfit.HasValue ? (double)request.Intent.TakeProfit.Value.Value : 0.0
        };
        if (!connected)
        {
            _pendingCommands.Enqueue(cmd);
            OnStatusChange?.Invoke("NETMQ_QUEUED", $"ORDER QUEUED (cBot not yet connected): {request.Direction} {request.Lots:F2} {request.Symbol.Value}");
            return Task.FromResult(clientOrderId);
        }
        lock (_bufferLock) { _bufferedCommands.Add(cmd); }
        OnStatusChange?.Invoke("NETMQ_BUFFERED", $"ORDER BUFFERED for bar: {request.Direction} {request.Lots:F2} {request.Symbol.Value}");
        return Task.FromResult(clientOrderId);
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
        if (!_transport.IsConnected)
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

    public Task ClosePartialPositionAsync(Guid positionId, decimal lots, CancellationToken ct)
    {
        if (!_transport.IsConnected) return Task.CompletedTask;
        var cmd = new { type = "close_partial", positionId = positionId.ToString(), lots };
        lock (_bufferLock) { _bufferedCommands.Add(cmd); }
        return Task.CompletedTask;
    }

    public Task SendShutdownAsync(CancellationToken ct)
    {
        if (!IsConnected)
        {
            _logger.LogWarning("CTRADER|SHUTDOWN_DROPPED|reason=not connected");
            return Task.CompletedTask;
        }
        var json = JsonSerializer.Serialize(new { type = "shutdown" });
        _transport.Send(_cBotIdentity!, json);
        return Task.CompletedTask;
    }

    public void RegisterConnectedHandler(Action handler) => OnConnected = handler;

    public Task CompleteBarAsync(CancellationToken ct) => CompleteBarAsync(CurrentBarSeq, ct);

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

        if (IsConnected && _cBotIdentity is not null)
        {
            _transport.Send(_cBotIdentity, json);
            _commandsSent += commands.Length;
            _journal?.Write("ORDER_SENT", null, DateTime.UtcNow,
                JsonSerializer.Serialize(new { seq, count = commands.Length }, JsonOpts));
            _logger.LogInformation("CTRADER|BAR_DONE|seq={Seq}|commands={Count}", seq, commands.Length);
            OnStatusChange?.Invoke("BAR_DONE", $"BAR_DONE seq={seq} commands={commands.Length}");
        }
        else
        {
            _logger.LogWarning("CTRADER|BAR_DONE_FAILED|seq={Seq}|reason=cBot not connected", seq);
        }

        await Task.CompletedTask;
    }

    public Task<AccountState> GetAccountStateAsync(CancellationToken ct)
        => Task.FromResult(new AccountState(0, 0, []));

    private static decimal? ParseDecimalOrNull(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetDecimal();
        return null;
    }

    private void TryWriteExec(ExecutionEvent exec)
    {
        var sig = $"{exec.OrderId}|{exec.NewState}|{exec.FillPrice?.Value ?? 0}|{exec.FilledLots}";
        lock (_recentExecSigs)
        {
            if (!_recentExecSigs.Add(sig))
            {
                Interlocked.Increment(ref _execsDeduped);
                return;
            }
            // Bounded LRU: evict the single oldest signature, never the whole set — a full clear
            // would let a re-sent duplicate slip through and be applied twice.
            _recentExecOrder.Enqueue(sig);
            if (_recentExecOrder.Count > MaxRecentExecSigs)
                _recentExecSigs.Remove(_recentExecOrder.Dequeue());
        }
        _execChannel.Writer.TryWrite(exec);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync(CancellationToken.None);
}
