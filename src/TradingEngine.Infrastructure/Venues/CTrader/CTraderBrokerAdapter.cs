using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain;

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
    public ITransportStatusSource? TransportStatus => _transport as ITransportStatusSource;

    /// <summary>Fired on every cBot hello (connect + reconnect) with the venue-reported open
    /// positions and account, so the engine can reconcile its position tracker (V1/V2).</summary>
    public Action<AccountState>? OnReconcile { get; set; }

    /// <summary>Fired when the venue confirms a stop-loss/take-profit modify (V3 writeback).</summary>
    public Action<Guid, Price, Price?>? OnStopModified { get; set; }

    // Last venue-authoritative account snapshot, parsed from the cBot hello. Returned by
    // GetAccountStateAsync so startup/reconnect reconciliation sees real open positions
    // instead of the old (0,0,[]) stub (V1).
    private volatile AccountState _lastKnownState = new(0, 0, []);
    private decimal _lastMid = 1.0m;

    public DateTime BrokerTimeUtc { get; private set; } = DateTime.UtcNow;
    public bool IsConnected => _transport.IsConnected;

    // iter-redesign-ctrader P1: cTrader owns exit execution. The broker holds real SL/TP orders,
    // triggers them server-side, and the cBot reports closes with reason (SL/TP/STOPOUT/…).
    // The engine never detects exits bar-by-bar — it reconciles to the venue's open set.
    public ExitMode ExitMode => ExitMode.VenueManaged;

    // iter-redesign-ctrader P2.1: the venue's authoritative open position set, built from
    // the cBot's clientOrderId ledger (populated on position open, removed on close).
    private readonly ConcurrentDictionary<Guid, byte> _openPositionIds = new();
    public IReadOnlySet<Guid> GetOpenPositionIds() => _openPositionIds.Keys.ToHashSet();

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
        finally
        {
            _tickChannel.Writer.TryComplete();
            _accountChannel.Writer.TryComplete();
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

                            // V1/V2 — capture the venue's open-position snapshot and reconcile.
                            var state = ParseHelloState(doc.RootElement);
                            _lastKnownState = state;

                            // iter-redesign-ctrader P2.1: sync the open-position-id set from the
                            // venue-authoritative snapshot so per-bar reconciliation is accurate.
                            _openPositionIds.Clear();
                            foreach (var op in state.OpenPositions)
                                _openPositionIds.TryAdd(op.PositionId, 0);

                            if (state.OpenPositions.Count > 0 || state.Balance > 0)
                            {
                                _logger.LogInformation("CTRADER|RECONCILE_SNAPSHOT|balance={Balance}|equity={Equity}|positions={Count}",
                                    state.Balance, state.Equity, state.OpenPositions.Count);
                                OnReconcile?.Invoke(state);
                            }

                            // V5 — a hello means (re)connection; re-flush any commands that were
                            // queued while disconnected so they ride the next bar_done envelope.
                            // (The transport's OnConnected only fires on the first identity, so a
                            // reconnect would otherwise never re-flush.)
                            FlushPendingCommands();

                            // iter-ctrader-capture: parse v=2 session metadata (mode)
                            // so the listen service can mint a RunId from a desktop-cTrader session.
                            var mode = doc.RootElement.TryGetProperty("mode", out var md) ? md.GetString() : null;
                            if (mode is not null)
                            {
                                var helloSymbols = doc.RootElement.TryGetProperty("symbols", out var syms)
                                    ? syms.EnumerateArray().Select(s => s.GetString()!).ToArray() : Array.Empty<string>();
                                var helloPeriods = doc.RootElement.TryGetProperty("periods", out var pers)
                                    ? pers.EnumerateArray().Select(p => p.GetString()!).ToArray() : Array.Empty<string>();
                                var sessionInfo = new SessionInfo(
                                    helloSymbols, helloPeriods, state.Balance, state.Equity, mode);
                                _logger.LogInformation(
                                    "CTRADER|SESSION|mode={Mode}|symbols={Symbols}|periods={Periods}|balance={Balance}",
                                    mode, string.Join(",", helloSymbols), string.Join(",", helloPeriods),
                                    state.Balance);
                                _onSessionStarted?.Invoke(sessionInfo);
                                _journal?.Write("SESSION_STARTED", null, DateTime.UtcNow);
                            }
                            break;

                        case "bar":
                            CurrentBarSeq = doc.RootElement.TryGetProperty("seq", out var s) ? s.GetInt64() : 0;
                            _barsReceived++;
                            decimal? spread = doc.RootElement.TryGetProperty("spread", out var sp)
                                ? sp.GetDecimal() : null;
                            var bar = new Bar(
                                Symbol.Parse(doc.RootElement.GetProperty("symbol").GetString()!),
                                Enum.Parse<Timeframe>(doc.RootElement.GetProperty("period").GetString()!, ignoreCase: true),
                                doc.RootElement.GetProperty("openTime").GetDateTime().ToUniversalTime(),
                                doc.RootElement.GetProperty("open").GetDecimal(),
                                doc.RootElement.GetProperty("high").GetDecimal(),
                                doc.RootElement.GetProperty("low").GetDecimal(),
                                doc.RootElement.GetProperty("close").GetDecimal(),
                                doc.RootElement.GetProperty("volume").GetDouble(),
                                spread);
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
        finally
        {
            _barChannel.Writer.TryComplete();
            _execChannel.Writer.TryComplete();
        }
    }

    private void DispatchSubMessage(string topic, JsonElement root)
    {
        switch (topic)
        {
            case "tick":
            {
                var bid = root.GetProperty("bid").GetDecimal();
                var ask = root.GetProperty("ask").GetDecimal();
                _lastMid = (bid + ask) / 2m;
                var tick = new Tick(
                    Symbol.Parse(root.GetProperty("symbol").GetString()!),
                    bid, ask,
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
                if (TryHandleModifyConfirmation(ex)) { continue; }
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
        if (TryHandleModifyConfirmation(root)) { return; }
        var exec = ParseExecution(root);
        TryWriteExec(exec);
        _execsReceived++;
    }

    // V3 — a venue-confirmed SL/TP modification. Routed to OnStopModified (writeback) instead
    // of the execution stream, which only carries fills/closes/rejections.
    private bool TryHandleModifyConfirmation(JsonElement ex)
    {
        var kind = ex.TryGetProperty("kind", out var k) ? k.GetString() : null;
        if (kind != "modify") return false;

        var orderId = ex.TryGetProperty("clientOrderId", out var oid) && oid.GetString() is { } oidStr
            ? Guid.Parse(oidStr) : Guid.Empty;
        var state = ex.TryGetProperty("state", out var st) ? st.GetString() : null;
        if (orderId == Guid.Empty || state != "Filled")
        {
            _logger.LogWarning("CTRADER|MODIFY_REJECTED|order={Order}|state={State}", orderId, state);
            return true;
        }

        var newSl = ParseDecimalOrNull(ex, "slPrice");
        var newTp = ParseDecimalOrNull(ex, "tpPrice");
        if (newSl is > 0m)
        {
            var tp = newTp is > 0m ? new Price(newTp.Value) : (Price?)null;
            _logger.LogInformation("CTRADER|MODIFY_CONFIRMED|order={Order}|sl={Sl}|tp={Tp}", orderId, newSl, newTp);
            OnStopModified?.Invoke(orderId, new Price(newSl.Value), tp);
        }
        return true;
    }

    private AccountState ParseHelloState(JsonElement root)
    {
        var balance = root.TryGetProperty("account", out var acct) && acct.TryGetProperty("balance", out var b)
            ? b.GetDecimal() : 0m;
        var equity = root.TryGetProperty("account", out var acct2) && acct2.TryGetProperty("equity", out var e)
            ? e.GetDecimal() : 0m;

        var positions = new List<OpenPositionInfo>();
        if (root.TryGetProperty("positions", out var pos) && pos.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pos.EnumerateArray())
            {
                if (!p.TryGetProperty("clientOrderId", out var cid) || cid.GetString() is not { } cidStr
                    || !Guid.TryParse(cidStr, out var orderId))
                {
                    continue; // a position the engine never opened (no durable Guid) — cannot manage it
                }

                var symbol = Symbol.Parse(p.GetProperty("symbol").GetString()!);
                var direction = Enum.Parse<TradeDirection>(p.GetProperty("direction").GetString()!, ignoreCase: true);
                var lots = p.GetProperty("lots").GetDecimal();
                var entry = p.GetProperty("entryPrice").GetDecimal();
                var sl = p.TryGetProperty("stopLoss", out var slEl) ? slEl.GetDecimal() : 0m;
                var tp = p.TryGetProperty("takeProfit", out var tpEl) ? tpEl.GetDecimal() : 0m;

                positions.Add(new OpenPositionInfo(
                    orderId, symbol, direction, lots, new Price(entry),
                    new Price(sl > 0m ? sl : entry),
                    tp > 0m ? new Price(tp) : null));
            }
        }

        return new AccountState(balance, equity, positions);
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
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to format reconcile line"); }
    }

    private ExecutionEvent ParseExecution(JsonElement ex)
    {
        var orderId = ex.TryGetProperty("clientOrderId", out var oid) && oid.GetString() is { } oidStr
            ? Guid.Parse(oidStr) : Guid.Empty;
        var state = Enum.Parse<OrderState>(ex.GetProperty("state").GetString()!, ignoreCase: true);
        var fillPrice = ex.GetProperty("fillPrice").GetDecimal();
        var filledLots = ex.GetProperty("filledLots").GetDecimal();
        var reason = ex.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
        var time = ex.TryGetProperty("simTime", out var st) ? st.GetDateTime().ToUniversalTime() : BrokerTimeUtc;
        return new ExecutionEvent(orderId, state,
            fillPrice > 0 ? new Price(fillPrice) : null,
            filledLots, reason, time)
        {
            GrossProfit = ParseDecimalOrNull(ex, "grossProfit"),
            NetProfit = ParseDecimalOrNull(ex, "netProfit"),
            Commission = ParseDecimalOrNull(ex, "commission"),
            Swap = ParseDecimalOrNull(ex, "swap"),
            CloseReason = ex.TryGetProperty("closeReason", out var cr) && cr.ValueKind == JsonValueKind.String
                ? cr.GetString() : null,
        };
    }

    // Pending commands (queued while disconnected, or re-queued from a failed bar-done) are moved
    // back into the per-bar buffer so they ride the NEXT bar_done envelope. They are never sent as
    // standalone messages — the cBot only processes commands inside a bar_done, so an individual
    // send would be silently dropped (V5).
    private void FlushPendingCommands()
    {
        if (!IsConnected) return;
        var moved = 0;
        while (_pendingCommands.TryDequeue(out var cmd))
        {
            lock (_bufferLock) { _bufferedCommands.Add(cmd); }
            moved++;
        }
        if (moved > 0)
            _logger.LogInformation("CTRADER|FLUSH_PENDING|requeued={Count}|destination=next_bar_done", moved);
    }

    public Task<Guid> SubmitOrderAsync(OrderRequest request, CancellationToken ct)
    {
        // Use the engine's order id (= kernel PositionId) as the venue clientOrderId so cBot fills/closes
        // and the trade-ledger reconciliation all key off the SAME id the kernel uses (iter-36 K2/K4).
        var clientOrderId = request.ClientOrderId ?? Guid.NewGuid();
        var connected = IsConnected;
        _logger.LogInformation("CTRADER|SUBMIT_ORDER|id={Id}|symbol={Symbol}|dir={Dir}|lots={Lots}|connected={Connected}",
            clientOrderId, request.Symbol, request.Direction, request.Lots, connected);
        var entryOpts = request.Intent.Entry;
        // NOTE (found while implementing P2.7, deliberately NOT changed here): entryOpts is always null on
        // the kernel path (EffectExecutor.SubmitOrder rebuilds a bare TradeIntent with no Entry attached),
        // so isLimit below is always false there regardless of request.Type — every kernel-path order has
        // ALWAYS gone out to cTrader as "Market", even a domain Limit order. Confirmed by re-deriving
        // orderType from request.Type directly (matching both replay venues) and watching two real
        // cTrader-CLI E2E tests (EurUsd_H1_3Days_ProducesTrades, EurUsd_H1_ThreeMonth_GeneratesAtLeastOneTrade)
        // drop from producing trades to zero — flipping every strategy's LimitOffset-configured signal from
        // an always-fills Market order to a real resting Limit order (2-pip offset, ~3-bar expiry) that,
        // via the real cTrader-cli replay, essentially never fills. That is a genuine, previously-latent
        // cTrader-integration gap (a Limit order submitted through the kernel path has apparently never
        // actually rested at cTrader) — real, but out of scope for "add Stop orders"; flagging it here
        // instead of silently fixing it mid-phase. isLimit is kept exactly as it was; only Stop (a brand
        // new order type with no existing callers) is added, so there is zero behavior change for any
        // strategy shipped today.
        var isLimit = entryOpts?.Method == OrderEntryMethod.LimitOffset;
        var isStop = request.Type == OrderType.Stop;
        var orderTypeStr = isStop ? "Stop" : isLimit ? "Limit" : "Market";
        var isResting = isLimit || isStop;
        var cmd = new
        {
            type = "submit_order",
            clientOrderId = clientOrderId.ToString(),
            symbol = request.Symbol.Value,
            direction = request.Direction.ToString(),
            lots = (double)request.Lots,
            orderType = orderTypeStr,
            limitPrice = isResting ? (double)request.Intent.LimitPrice!.Value.Value : 0.0,
            expiryBars = isResting ? (entryOpts?.LimitOrderExpiryBars ?? 3) : 0,
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
                    new Price(_lastMid), 0, "FORCE_CLOSE_ENGINE_SHUTDOWN", BrokerTimeUtc)
                {
                    GrossProfit = 0m, NetProfit = 0m, Commission = 0m, Swap = 0m
                });
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
            // V5 — disconnected at bar-done. Do NOT drop the drained commands: re-queue them so the
            // next reconnect (FlushPendingCommands → buffer → next bar_done) still delivers them.
            foreach (var cmd in commands)
                _pendingCommands.Enqueue(cmd);
            _logger.LogWarning("CTRADER|BAR_DONE_FAILED|seq={Seq}|reason=cBot not connected|requeued={Count}",
                seq, commands.Length);
        }

        await Task.CompletedTask;
    }

    // V1 — returns the latest venue snapshot parsed from the cBot hello (open positions + account),
    // not the old (0,0,[]) stub. Empty until the first hello arrives.
    public Task<AccountState> GetAccountStateAsync(CancellationToken ct)
        => Task.FromResult(_lastKnownState);

    public void RegisterReconcileHandler(Action<AccountState> handler) => OnReconcile = handler;
    public void RegisterStopModifiedHandler(Action<Guid, Price, Price?> handler) => OnStopModified = handler;

    private Action<SessionInfo>? _onSessionStarted;
    public void RegisterSessionStartedHandler(Action<SessionInfo> handler) => _onSessionStarted = handler;

    private static decimal? ParseDecimalOrNull(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetDecimal();
        return null;
    }

    private static DateTime? TryParseUtc(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToUniversalTime();
        }
        return null;
    }

    private void TryWriteExec(ExecutionEvent exec)
    {
        var sig = $"{exec.OrderId}|{exec.NewState}|{exec.FillPrice?.Value ?? 0}|{exec.FilledLots}|{exec.GrossProfit}|{exec.NetProfit}|{exec.Commission}|{exec.Swap}";
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

        // iter-redesign-ctrader P2.1: keep the open-position-id set in sync with execution events.
        // Entry fills add; close fills (carrying CloseReason) remove.
        if (exec.NewState == OrderState.Filled && exec.FillPrice is not null)
        {
            if (exec.CloseReason is not null)
                _openPositionIds.TryRemove(exec.OrderId, out _);
            else if (exec.FilledLots > 0)
                _openPositionIds.TryAdd(exec.OrderId, 0);
        }

        _execChannel.Writer.TryWrite(exec);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync(CancellationToken.None);
}
