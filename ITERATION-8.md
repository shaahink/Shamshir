# ITERATION 8 — NetMQ Transport, Bar-Close Evaluation, Observability

## Mandatory Reading Before Touching Any Code

Read in this order. The plan's file paths and code patterns are based on the state of these files right now.

1. `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` — current cBot: uses `PrintEnvelope` / stdout. Replace with NetMQ PUB. Keep `OnBar()` override structure, replace `OnTick()`, wire `bars.BarClosed`.
2. `src/TradingEngine.Infrastructure/Adapters/NamedPipeBrokerAdapter.cs` — the full channel/parsing pattern to replicate in `NetMQBrokerAdapter`. Then delete it.
3. `src/TradingEngine.Host/EngineWorker.cs` — strategy evaluation loop in `ProcessTicksAsync` moves to `ProcessBarsAsync`. Tick loop becomes lightweight.
4. `src/TradingEngine.Host/Program.cs` — adapter wiring (Live/Paper → NamedPipeBrokerAdapter) is replaced with NetMQBrokerAdapter.
5. `src/TradingEngine.CTraderRunner/BacktestRunner.cs` — pipe references are replaced with port-based readiness probe.
6. `src/TradingEngine.Infrastructure/TradingEngine.Infrastructure.csproj` — add NetMQ NuGet here.
7. `src/TradingEngine.Adapters.CTrader/TradingEngine.Adapters.CTrader.csproj` — add NetMQ NuGet here, remove Newtonsoft.Json.
8. `tests/TradingEngine.Tests.Simulation/Pipeline/FullBacktestPipelineTest.cs` — update to work with NetMQ; add 3-day fast variant.

The POC that proves NetMQ works inside ctrader-cli is at `CTraderNetMQPOC/` (separate directory, not part of the main solution). Read `CTraderNetMQPOC/POC-REPORT.md` (or the embedded report in this file) for the edge cases already solved — specifically: slow joiner fix, ROUTER identity capture, polymorphic JSON serialization, and the `cbotset` cache invalidation issue.

Do not start coding until you have read all eight files above.

---

## Context

The current transport between cBot and engine is stdout `Print("DATA|{json}")` — a one-way, polling-based mechanism that cannot carry commands back to the cBot (no order execution in the realistic backtest path). The named pipe approach was abandoned because `.NET managed sockets` (`TcpClient`, `NamedPipeClientStream`) are intercepted inside `ctrader-cli`'s hosting sandbox.

A POC has proven that **NetMQ (ZeroMQ)** works inside `ctrader-cli` because it uses native P/Invoke socket calls, bypassing the .NET interception. The POC delivered 1,461 messages in 25 seconds over two channels: PUB/SUB for data (cBot→engine) and ROUTER/DEALER for commands (engine→cBot, bidirectional).

In parallel, strategy evaluation currently runs on **every tick** in `ProcessTicksAsync`. Since indicators are only recomputed on bar close (`ProcessBarsAsync`), the tick-level evaluation produces identical results on every tick within a bar — wasted CPU and log noise. Moving evaluation to bar close is architecturally correct and dramatically simplifies the hot path.

This iteration replaces the transport, moves evaluation to bar-close, adds structured observability, and removes all dead pipe code.

---

## Architecture After This Iteration

```
ctrader-cli (backtest process)          Engine process (TradingEngine.Host)
┌──────────────────────────────┐        ┌────────────────────────────────────┐
│  TradingEngineCBot           │        │  NetMQBrokerAdapter                │
│                              │        │                                    │
│  PUB socket (bind :15555) ───┼──────► SUB socket (connect :15555)         │
│    topic "bar"  → Bar OHLC  │        │   → Channel<Bar>                   │
│    topic "tick" → Bid/Ask   │        │   → Channel<Tick>                  │
│    topic "acct" → Balance   │        │   → Channel<AccountUpdate>         │
│    topic "exec" → Fills     │        │   → Channel<ExecutionEvent>        │
│                              │        │                                    │
│  DEALER socket (connect      │        │  ROUTER socket (bind :15556)       │
│              :15556) ◄───────┼──────── commands: SubmitOrder, Close, etc. │
└──────────────────────────────┘        └────────────────────────────────────┘
                                                        │
                                              EngineWorker
                                           ProcessBarsAsync
                                             ↓ bar arrives
                                             ↓ RecomputeIndicators
                                             ↓ EvaluateStrategies (once per bar)
                                             ↓ DispatchOrder → NetMQBrokerAdapter
```

**IBrokerAdapter is unchanged.** The engine stays fully agnostic to transport.

---

## Phase 1 — NetMQ cBot

**Goal**: Replace `PrintEnvelope` with NetMQ PUB socket. Add DEALER for command receipt. Clean up all dead event stubs.

### 1a. Project file

`src/TradingEngine.Adapters.CTrader/TradingEngine.Adapters.CTrader.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="cTrader.Automate" Version="*" />
  <PackageReference Include="NetMQ" Version="4.*" />
  <!-- Remove: Newtonsoft.Json — no longer needed -->
</ItemGroup>
```

Remove `Newtonsoft.Json`. Remove `MessageSerializer.cs` (now unused), along with `PipeMessage.cs`, `PipeClient.cs`, `TickPublisher.cs`, `BarPublisher.cs`, `AccountUpdatePublisher.cs`, `ExecutionEventPublisher.cs`, `OrderCommandHandler.cs`. These are all dead after this phase.

### 1b. New TradingEngineCBot.cs

Replace the entire file:

```csharp
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using cAlgo.API;
using NetMQ;
using NetMQ.Sockets;

namespace TradingEngine.Adapters.CTrader;

[Robot(AccessRights = AccessRights.FullAccess)]
public class TradingEngineCBot : Robot
{
    [Parameter("Data Port", DefaultValue = "15555")]
    public int DataPort { get; set; } = 15555;

    [Parameter("Command Port", DefaultValue = "15556")]
    public int CommandPort { get; set; } = 15556;

    [Parameter("Tick Every N", DefaultValue = "10")]
    public int TickEveryN { get; set; } = 10;

    private PublisherSocket? _pub;
    private DealerSocket? _dealer;
    private NetMQPoller? _poller;
    private readonly ConcurrentQueue<Action> _mainActions = new();
    private int _tickCounter;

    private static readonly JsonSerializerOptions JsonOpts = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    protected override void OnStart()
    {
        Print($"CBOT|START|symbol={SymbolName}|tf={TimeFrame.ShortName}|dataPort={DataPort}|cmdPort={CommandPort}");

        _pub = new PublisherSocket();
        _pub.Bind($"tcp://*:{DataPort}");

        _dealer = new DealerSocket();
        _dealer.Connect($"tcp://127.0.0.1:{CommandPort}");
        _dealer.ReceiveReady += OnDealerReceive;

        _poller = new NetMQPoller { _dealer };
        _poller.RunAsync();

        // Slow-joiner fix: give SUB side time to complete handshake before publishing
        System.Threading.Thread.Sleep(600);

        // Send hello so engine ROUTER captures our identity
        _dealer.SendFrame(Serialize("hello", new { }));

        // Subscribe to bar events — BarClosed fires for history + backtest replay with real OHLC
        var bars = MarketData.GetBars(TimeFrame, SymbolName);
        bars.BarClosed += OnBarClosed;

        // Publish initial account snapshot
        PublishAccount();

        Print($"CBOT|READY|dataPort={DataPort}|cmdPort={CommandPort}");
    }

    protected override void OnTick()
    {
        _tickCounter++;

        // Drain commands queued from poller thread — must execute on cBot main thread
        while (_mainActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Print($"CBOT|CMD_ERR|{ex.Message}"); }
        }

        // Publish tick (throttled)
        if (_tickCounter % TickEveryN == 0)
        {
            Publish("tick", new
            {
                symbol = SymbolName,
                bid = Symbol.Bid,
                ask = Symbol.Ask,
                time = Server.TimeInUtc.ToString("o")
            });

            PublishAccount();
        }
    }

    private void OnBarClosed(BarClosedEventArgs args)
    {
        var bars = args.Bars;
        // Last(1) = most recently closed bar; Last(0) is the forming one
        var bar = bars.Last(1);
        if (bar.Open == 0 && bar.High == 0) return; // skip placeholder bars

        Publish("bar", new
        {
            symbol = bars.SymbolName,
            period = TimeFrame.ShortName,
            openTime = bar.OpenTime.ToString("o"),
            open = bar.Open,
            high = bar.High,
            low = bar.Low,
            close = bar.Close,
            volume = (long)bar.TickVolume
        });

        Print($"CBOT|BAR|{bars.SymbolName}|{TimeFrame.ShortName}|{bar.OpenTime:yyyy-MM-dd HH:mm}|close={bar.Close:F5}");
    }

    private void OnDealerReceive(object? sender, NetMQSocketEventArgs e)
    {
        if (!e.Socket.TryReceiveFrameString(out var json) || json is null) return;
        var captured = json;
        _mainActions.Enqueue(() => HandleCommand(captured));
    }

    private void HandleCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();
            Print($"CBOT|CMD|{type}|{json[..Math.Min(120, json.Length)]}");

            switch (type)
            {
                case "submit_order":   HandleSubmitOrder(doc.RootElement);   break;
                case "close_position": HandleClosePosition(doc.RootElement); break;
                case "modify_order":   HandleModifyOrder(doc.RootElement);   break;
                case "cancel_order":   HandleCancelOrder(doc.RootElement);   break;
            }
        }
        catch (Exception ex)
        {
            Print($"CBOT|CMD_PARSE_ERR|{ex.Message}");
        }
    }

    private void HandleSubmitOrder(JsonElement cmd)
    {
        var clientOrderId = cmd.GetProperty("clientOrderId").GetGuid();
        var symbol        = cmd.GetProperty("symbol").GetString()!;
        var direction     = cmd.GetProperty("direction").GetString()!;
        var lots          = cmd.GetProperty("lots").GetDouble();
        var slPrice       = cmd.GetProperty("slPrice").GetDouble();
        var tpPrice       = cmd.GetProperty("tpPrice").GetDouble();

        var sym = Symbols.GetSymbol(symbol);
        if (sym is null)
        {
            PublishExec(clientOrderId, "Rejected", 0, 0, "Unknown symbol: " + symbol);
            return;
        }

        var tradeType = direction == "Long" ? TradeType.Buy : TradeType.Sell;
        var volumeInUnits = Math.Floor(lots * sym.LotSize / sym.VolumeInUnitsStep) * sym.VolumeInUnitsStep;
        var slPips = slPrice > 0 ? (double?)Math.Abs(slPrice - (sym.Bid + sym.Ask) / 2.0) / sym.PipSize : null;
        var tpPips = tpPrice > 0 ? (double?)Math.Abs(tpPrice - (sym.Bid + sym.Ask) / 2.0) / sym.PipSize : null;

        var result = ExecuteMarketOrder(tradeType, symbol, volumeInUnits, "Shamshir", slPips, tpPips);
        if (result?.IsSuccessful == true)
        {
            var pos = result.Position;
            PublishExec(clientOrderId, "Filled", pos.EntryPrice, pos.VolumeInUnits / sym.LotSize, null);
            PublishAccount();
        }
        else
        {
            PublishExec(clientOrderId, "Rejected", 0, 0, result?.Error.ToString() ?? "Null result");
        }
    }

    private void HandleClosePosition(JsonElement cmd)
    {
        var positionId = cmd.GetProperty("positionId").GetString();
        foreach (var pos in Positions)
        {
            if (pos.Id.ToString() == positionId)
            {
                var result = ClosePosition(pos);
                if (result?.IsSuccessful == true) PublishAccount();
                return;
            }
        }
        Print($"CBOT|CLOSE_NOT_FOUND|positionId={positionId}");
    }

    private void HandleModifyOrder(JsonElement cmd)
    {
        var orderId = cmd.GetProperty("orderId").GetString();
        var newSl   = cmd.GetProperty("newSl").GetDouble();
        var newTp   = cmd.GetProperty("newTp").GetDouble();
        foreach (var pos in Positions)
        {
            if (pos.Id.ToString() == orderId)
            {
#pragma warning disable CS0618
                ModifyPosition(pos, newSl > 0 ? newSl : pos.StopLoss, newTp > 0 ? newTp : pos.TakeProfit);
#pragma warning restore CS0618
                return;
            }
        }
    }

    private void HandleCancelOrder(JsonElement cmd)
    {
        var orderId = cmd.GetProperty("orderId").GetString();
        foreach (var order in PendingOrders)
        {
            if (order.Id.ToString() == orderId)
            {
                CancelPendingOrder(order);
                return;
            }
        }
    }

    protected override void OnStop()
    {
        Print($"CBOT|STOP|ticks={_tickCounter}");
        _poller?.StopAsync();
        _dealer?.Dispose();
        _pub?.Dispose();
        _poller?.Dispose();
        NetMQConfig.Cleanup(false);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private void Publish(string topic, object payload)
    {
        if (_pub is null) return;
        var json = Serialize(topic, payload);
        _pub.SendMoreFrame(topic).SendFrame(json);
    }

    private void PublishAccount()
    {
        Publish("acct", new
        {
            balance    = Account.Balance,
            equity     = Account.Equity,
            floatingPnL = Account.Equity - Account.Balance,
            time       = Server.TimeInUtc.ToString("o")
        });
    }

    private void PublishExec(Guid clientOrderId, string state, double fillPrice, double filledLots, string? reason)
    {
        Publish("exec", new
        {
            clientOrderId = clientOrderId.ToString(),
            state,
            fillPrice,
            filledLots,
            reason,
            time = Server.TimeInUtc.ToString("o")
        });
    }

    private static string Serialize(string type, object payload)
    {
        // Embed type discriminator at top level alongside payload fields.
        // Use anonymous wrapper; actual serialization uses runtime type.
        var dict = new System.Collections.Generic.Dictionary<string, object>(8)
            { ["type"] = type };
        // Merge payload fields — serialize payload, re-parse, copy properties
        using var payloadDoc = JsonDocument.Parse(JsonSerializer.Serialize(payload, payload.GetType(), JsonOpts));
        foreach (var prop in payloadDoc.RootElement.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();
        return JsonSerializer.Serialize(dict, JsonOpts);
    }
}
```

**Key design decisions in this cBot:**
- `bars.BarClosed` (not `OnBar()`) to capture ALL bars including pre-backtest warmup history
- Skip bars where `Open == 0` (BarOpened placeholders that slip through)
- `OnTick()` override (not `bars.Tick`) — cleaner, runs on main thread
- Command queue drained in `OnTick()` to stay on the cBot main thread
- `TickEveryN` configurable to balance data fidelity vs throughput

---

## Phase 2 — NetMQBrokerAdapter

**Goal**: New `IBrokerAdapter` implementation. SUB socket connects to cBot's PUB. ROUTER socket binds for commands. Writes to channels that `EngineWorker` already reads.

**File**: `src/TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs` (new file)

Add NuGet to `src/TradingEngine.Infrastructure/TradingEngine.Infrastructure.csproj`:
```xml
<PackageReference Include="NetMQ" Version="4.*" />
```

```csharp
using System.Text.Json;
using System.Threading.Channels;
using NetMQ;
using NetMQ.Sockets;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class NetMQBrokerAdapter : IBrokerAdapter, IAsyncDisposable
{
    private readonly string _dataEndpoint;    // e.g. "tcp://127.0.0.1:15555"
    private readonly string _commandEndpoint; // e.g. "tcp://*:15556"
    private readonly ILogger<NetMQBrokerAdapter> _logger;

    private readonly Channel<Tick>           _tickChannel    = Channel.CreateBounded<Tick>(new BoundedChannelOptions(10_000)    { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<Bar>            _barChannel     = Channel.CreateBounded<Bar>(new BoundedChannelOptions(2_000)      { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<AccountUpdate>  _accountChannel = Channel.CreateBounded<AccountUpdate>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<ExecutionEvent> _execChannel    = Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });

    public ChannelReader<Tick>           TickStream      => _tickChannel.Reader;
    public ChannelReader<Bar>            BarStream       => _barChannel.Reader;
    public ChannelReader<AccountUpdate>  AccountStream   => _accountChannel.Reader;
    public ChannelReader<ExecutionEvent> ExecutionStream => _execChannel.Reader;

    public DateTime BrokerTimeUtc { get; private set; } = DateTime.UtcNow;
    public bool IsConnected => _cBotIdentity is not null;

    private SubscriberSocket? _sub;
    private RouterSocket?     _router;
    private NetMQPoller?      _poller;
    private byte[]?           _cBotIdentity; // captured from first DEALER hello

    private static readonly JsonSerializerOptions JsonOpts = new()
        { PropertyNameCaseInsensitive = true };

    public NetMQBrokerAdapter(string dataEndpoint, string commandEndpoint, ILogger<NetMQBrokerAdapter> logger)
    {
        _dataEndpoint    = dataEndpoint;
        _commandEndpoint = commandEndpoint;
        _logger          = logger;
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        _sub = new SubscriberSocket();
        _sub.Connect(_dataEndpoint);
        _sub.SubscribeToAnyTopic(); // receive all topics
        _sub.ReceiveReady += OnSubReceive;

        _router = new RouterSocket();
        _router.Bind(_commandEndpoint);
        _router.ReceiveReady += OnRouterReceive;

        _poller = new NetMQPoller { _sub, _router };
        _poller.RunAsync();

        _logger.LogInformation("NetMQ adapter started. Data={Data} Command={Command}", _dataEndpoint, _commandEndpoint);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        _poller?.StopAsync();
        _sub?.Dispose();
        _router?.Dispose();
        _poller?.Dispose();
        _tickChannel.Writer.TryComplete();
        _barChannel.Writer.TryComplete();
        _accountChannel.Writer.TryComplete();
        _execChannel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    private void OnSubReceive(object? sender, NetMQSocketEventArgs e)
    {
        try
        {
            var topic = e.Socket.ReceiveFrameString();
            var json  = e.Socket.ReceiveFrameString();
            using var doc = JsonDocument.Parse(json);
            DispatchMessage(topic, doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NetMQ SUB parse error");
        }
    }

    private void DispatchMessage(string topic, JsonElement root)
    {
        switch (topic)
        {
            case "tick":
            {
                var symbol = Symbol.Parse(root.GetProperty("symbol").GetString()!);
                var bid    = root.GetProperty("bid").GetDecimal();
                var ask    = root.GetProperty("ask").GetDecimal();
                var time   = root.GetProperty("time").GetDateTime().ToUniversalTime();
                BrokerTimeUtc = time;
                _tickChannel.Writer.TryWrite(new Tick(symbol, bid, ask, time));
                break;
            }
            case "bar":
            {
                var symbol  = Symbol.Parse(root.GetProperty("symbol").GetString()!);
                var tf      = Enum.Parse<Timeframe>(root.GetProperty("period").GetString()!, ignoreCase: true);
                var openTime = root.GetProperty("openTime").GetDateTime().ToUniversalTime();
                var bar = new Bar(symbol, tf, openTime,
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
                var orderId    = root.GetProperty("clientOrderId").GetGuid();
                var state      = Enum.Parse<OrderState>(root.GetProperty("state").GetString()!, ignoreCase: true);
                var fillPrice  = root.GetProperty("fillPrice").GetDecimal();
                var filledLots = root.GetProperty("filledLots").GetDecimal();
                var reason     = root.GetProperty("reason").ValueKind == JsonValueKind.String
                    ? root.GetProperty("reason").GetString() : null;
                var time       = root.GetProperty("time").GetDateTime().ToUniversalTime();
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
        // First frame = peer identity; second frame = message
        var identity = e.Socket.ReceiveFrameBytes();
        var json     = e.Socket.ReceiveFrameString();
        if (_cBotIdentity is null)
        {
            _cBotIdentity = identity;
            _logger.LogInformation("NetMQ cBot identity captured. IsConnected=true");
        }
        // Responses (execution events) from cBot come over PUB/SUB; ROUTER is command-only
    }

    public async Task<Guid> SubmitOrderAsync(OrderRequest request, CancellationToken ct)
    {
        var clientOrderId = Guid.NewGuid();
        await SendCommandAsync(new
        {
            type          = "submit_order",
            clientOrderId = clientOrderId.ToString(),
            symbol        = request.Symbol.Value,
            direction     = request.Direction.ToString(),
            lots          = (double)request.Lots,
            slPrice       = (double)request.Intent.StopLoss.Value,
            tpPrice       = request.Intent.TakeProfit.HasValue ? (double)request.Intent.TakeProfit.Value.Value : 0.0
        }, ct);
        return clientOrderId;
    }

    public Task ModifyOrderAsync(Guid orderId, Price newSl, Price? newTp, CancellationToken ct)
        => SendCommandAsync(new { type = "modify_order", orderId = orderId.ToString(), newSl = (double)newSl.Value, newTp = newTp.HasValue ? (double)newTp.Value.Value : 0.0 }, ct);

    public Task CancelOrderAsync(Guid orderId, CancellationToken ct)
        => SendCommandAsync(new { type = "cancel_order", orderId = orderId.ToString() }, ct);

    public Task ClosePositionAsync(Guid positionId, CancellationToken ct)
        => SendCommandAsync(new { type = "close_position", positionId = positionId.ToString() }, ct);

    private Task SendCommandAsync(object command, CancellationToken ct)
    {
        if (_router is null || _cBotIdentity is null)
        {
            _logger.LogWarning("Command sent before cBot connected — dropped");
            return Task.CompletedTask;
        }
        var json = JsonSerializer.Serialize(command, command.GetType(), JsonOpts);
        _router.SendMoreFrame(_cBotIdentity).SendFrame(json);
        return Task.CompletedTask;
    }

    public Task<AccountState> GetAccountStateAsync(CancellationToken ct)
        => Task.FromResult(new AccountState(0, 0, []));

    public async ValueTask DisposeAsync() => await DisconnectAsync(CancellationToken.None);
}
```

**Serialization note**: The `DispatchMessage` method uses `System.Text.Json` directly from the parsed `JsonElement`. No double-serialization. The cBot encodes flat JSON with camelCase keys; the adapter reads with `PropertyNameCaseInsensitive = true`. This avoids the `Payload-as-string` bug that plagued the old pipe protocol.

---

## Phase 3 — Program.cs and BacktestRunner

### 3a. Program.cs adapter wiring

Replace the `if (mode == EngineMode.Live || mode == EngineMode.Paper)` block:

```csharp
if (mode == EngineMode.Live || mode == EngineMode.Paper)
{
    var brokerType = builder.Configuration.GetValue<string>("Engine:Broker:Type") ?? "NetMQ";
    if (brokerType == "NetMQ")
    {
        var dataPort    = builder.Configuration.GetValue<int>("Engine:Broker:NetMQ:DataPort",    15555);
        var commandPort = builder.Configuration.GetValue<int>("Engine:Broker:NetMQ:CommandPort", 15556);
        builder.Services.AddSingleton<IBrokerAdapter>(sp => new NetMQBrokerAdapter(
            $"tcp://127.0.0.1:{dataPort}",
            $"tcp://*:{commandPort}",
            sp.GetRequiredService<ILogger<NetMQBrokerAdapter>>()));
    }
    else
    {
        // NamedPipe kept for Aspire/manual testing only
        var pipeName = builder.Configuration.GetValue<string>("Engine:Broker:PipeName") ?? "trading-engine";
        builder.Services.AddSingleton<IBrokerAdapter>(sp =>
            new NamedPipeBrokerAdapter(pipeName, sp.GetRequiredService<ILogger<NamedPipeBrokerAdapter>>()));
    }
}
```

Also remove the `StubClock` Backtest split — already fixed in a prior commit. Confirm `BrokerClock` is registered for all modes.

Remove the `if (_broker is NamedPipeBrokerAdapter pipeAdapter)` in `EngineWorker.ExecuteAsync` (Phase 4 will clean this up as part of the ProcessBarsAsync rework).

### 3b. BacktestRunner — port-based wiring

Replace the pipe-based `WaitForEngineReadyAsync` with a port probe. Replace `BuildArgs` pipe parameter with port parameters.

Key changes to `BacktestRunner`:

```csharp
public async Task<BacktestResult> RunAsync(BacktestConfig cfg, CancellationToken ct = default)
{
    var runId       = Guid.NewGuid().ToString("N")[..8];
    var dataPort    = _config.GetValue<int>("Engine:Broker:NetMQ:DataPort",    15555);
    var commandPort = _config.GetValue<int>("Engine:Broker:NetMQ:CommandPort", 15556);
    // ... rest unchanged
    await WaitForEngineReadyAsync(commandPort, TimeSpan.FromSeconds(30), ct);
    // pass ports to BuildArgs instead of pipeName
}

private static async Task WaitForEngineReadyAsync(int commandPort, TimeSpan timeout, CancellationToken ct)
{
    // Probe the engine's ROUTER (bind) by briefly connecting as DEALER
    var deadline = DateTime.UtcNow + timeout;
    var attempt  = 0;
    while (DateTime.UtcNow < deadline)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var probe = new NetMQ.Sockets.DealerSocket();
            probe.Connect($"tcp://127.0.0.1:{commandPort}");
            probe.Disconnect($"tcp://127.0.0.1:{commandPort}");
            return; // port is open
        }
        catch { }
        attempt++;
        await Task.Delay(300, ct);
    }
    throw new TimeoutException($"Engine command port {commandPort} not ready after {timeout.TotalSeconds:F0}s ({attempt} probes)");
}
```

Add `--DataPort={dataPort}` and `--CommandPort={commandPort}` to `BuildArgs`. Remove `--PipeName={pipeName}`. Remove the `using System.IO.Pipes` import. Add `using NetMQ.Sockets`.

Add `NetMQ` NuGet to `src/TradingEngine.CTraderRunner/TradingEngine.CTraderRunner.csproj`.

`StartEngine()`: pass `Engine__Broker__NetMQ__DataPort` and `Engine__Broker__NetMQ__CommandPort` env vars instead of `Engine__Broker__PipeName`.

---

## Phase 4 — Bar-Close Strategy Evaluation

**Goal**: Move strategy evaluation from `ProcessTicksAsync` (runs on every tick) to `ProcessBarsAsync` (runs once per bar, after indicators are recomputed).

### 4a. Why this is correct

Currently:
- `ProcessBarsAsync` receives a bar → calls `RecomputeIndicatorsAsync` (updates `_indicatorValues`)
- `ProcessTicksAsync` receives a tick → calls `strategy.Evaluate(context)` with those indicator values

Since indicators only change on bar close, evaluating on every tick produces the same result for all ticks within a bar. Evaluation belongs in `ProcessBarsAsync`, immediately after `RecomputeIndicatorsAsync`.

The entry order fills at the next tick after the signal (for real cTrader execution, cTrader handles fill timing; for simulated path, `SimulatedBrokerAdapter.OnTickReceived` handles fills on the tick after submission).

### 4b. ProcessBarsAsync after change

```csharp
private async Task ProcessBarsAsync(CancellationToken ct)
{
    try
    {
        _logger.LogDebug("Bar processor started");
        await foreach (var bar in _broker.BarStream.ReadAllAsync(ct))
        {
            Interlocked.Increment(ref _barCount);
            var byTf = _bars.GetOrAdd(bar.Symbol, _ => new());
            var list = byTf.GetOrAdd(bar.Timeframe, _ => new());
            int barCount;
            lock (list)
            {
                list.Add(bar);
                if (list.Count > MaxBarHistory)
                    list.RemoveAt(0);
                barCount = list.Count;
            }

            await RecomputeIndicatorsAsync(bar.Symbol, bar.Timeframe);

            _logger.LogInformation("BAR_EVAL|{Symbol}|{Tf}|{Close:F5}|bars={Count}|total={Total}",
                bar.Symbol.Value, bar.Timeframe, bar.Close, barCount, Interlocked.Read(ref _barCount));

            // Synthesize close tick — strategies use bar.Close as entry reference
            var halfSpread  = ResolveHalfSpread(bar.Symbol);
            var closeTick   = new Tick(bar.Symbol, bar.Close, bar.Close + halfSpread,
                                       bar.OpenTimeUtc + GetBarDuration(bar.Timeframe));
            var barSnapshot = BuildBarSnapshot(bar.Symbol);
            if (barSnapshot is null) continue;

            BuildIndicatorSnapshot(bar.Symbol);

            foreach (var strategy in _strategies)
            {
                var totalBars = barSnapshot.Values.Sum(b => b.Count);
                if (totalBars < strategy.RequiredBarCount)
                {
                    _logger.LogDebug("EVAL|{Strategy}|{Symbol}|NEED_BARS|have={Have}|need={Need}",
                        strategy.Id, bar.Symbol.Value, totalBars, strategy.RequiredBarCount);
                    continue;
                }

                var context = new MarketContext(bar.Symbol, closeTick, barSnapshot,
                    _reusableIndicatorDict, _clock.UtcNow);
                var intent  = strategy.Evaluate(context);

                if (intent is null)
                {
                    _logger.LogDebug("EVAL|{Strategy}|{Symbol}|NO", strategy.Id, bar.Symbol.Value);
                    continue;
                }

                _logger.LogInformation("SIGNAL|{Strategy}|{Symbol}|{Dir}|sl={SL:F5}|tp={TP}",
                    strategy.Id, bar.Symbol.Value, intent.Direction,
                    intent.StopLoss.Value, intent.TakeProfit?.Value.ToString("F5") ?? "none");
                _logger.LogInformation("SIGNAL_REASON|{Strategy}|{Reason}", strategy.Id, intent.Reason);

                var equity   = Volatile.Read(ref _currentEquity);
                var orderCtx = await _orderDispatcher.DispatchAsync(intent, equity, bar.Close, _broker, ct);
                if (orderCtx is null) continue;

                var orderReq = new OrderRequest(intent, orderCtx.Lots, intent.Symbol,
                    intent.Direction, OrderType.Market, intent.LimitPrice);
                _positionTracker.TrackOrder(orderCtx.OrderId, orderReq, orderCtx.RiskAmount);

                _logger.LogInformation("ORDER|{Strategy}|{OrderId}|{Dir}|lots={Lots}|entry={Entry:F5}",
                    strategy.Id, orderCtx.OrderId, intent.Direction, orderCtx.Lots, bar.Close);
            }
        }
    }
    catch (OperationCanceledException) { }
    _logger.LogDebug("Bar processor stopped");
}
```

Add private helpers to `EngineWorker`:
```csharp
private decimal ResolveHalfSpread(Symbol symbol)
{
    try { return _symbolRegistry.Get(symbol).TypicalSpread / 2m; }
    catch { return 0.00005m; }
}

private static TimeSpan GetBarDuration(Timeframe tf) => tf switch
{
    Timeframe.M1  => TimeSpan.FromMinutes(1),
    Timeframe.M5  => TimeSpan.FromMinutes(5),
    Timeframe.M15 => TimeSpan.FromMinutes(15),
    Timeframe.M30 => TimeSpan.FromMinutes(30),
    Timeframe.H1  => TimeSpan.FromHours(1),
    Timeframe.H4  => TimeSpan.FromHours(4),
    Timeframe.D1  => TimeSpan.FromDays(1),
    _             => TimeSpan.FromHours(1),
};
```

### 4c. ProcessTicksAsync after change

Remove the entire strategy evaluation loop from `ProcessTicksAsync`. Keep only:
- Execution event drain
- Risk force-close check
- Account update consumption
- `sim.OnTickReceived(tick)` (simulated path only)

Change tick logging from `LogInformation` to `LogDebug` (ticks are now noise, not signal):

```csharp
private async Task ProcessTicksAsync(CancellationToken ct)
{
    try
    {
        _logger.LogDebug("Tick processor started");
        await foreach (var tick in _broker.TickStream.ReadAllAsync(ct))
        {
            Interlocked.Increment(ref _tickCount);

            while (_executionEventChannel.Reader.TryRead(out var execEvent))
                _positionTracker.OnExecution(execEvent, _strategies);

            if (_riskManager.ConsumeForceClosePending())
            {
                _logger.LogCritical("Force-close triggered. Closing {Count} open positions",
                    _positionTracker.OpenPositions.Count);
                foreach (var (_, pos) in _positionTracker.OpenPositions.ToList())
                    await _broker.ClosePositionAsync(pos.Id, ct);
            }

            var accountUpdate = Interlocked.Exchange(ref _latestAccountUpdate, null);
            if (accountUpdate is not null)
                HandleAccountUpdate(accountUpdate);

            if (_dataFeed is not null && _broker is SimulatedBrokerAdapter sim)
                sim.OnTickReceived(tick);

            _logger.LogDebug("TICK|{Symbol}|{Bid:F5}|{Ask:F5}|{Total}",
                tick.Symbol.Value, tick.Bid, tick.Ask, Interlocked.Read(ref _tickCount));
        }
    }
    catch (OperationCanceledException) { }
    _logger.LogDebug("Tick processor stopped");
}
```

Also remove `await Task.Yield()` from tick loop — it was added to avoid starvation but is no longer needed since the tick loop is now lightweight.

### 4d. EngineWorker cleanup

Remove:
- `_reusableIndicatorDict` from the class (move it local to `ProcessBarsAsync` — it's only needed there now)
- Actually keep it as a field but stop clearing/rebuilding it on ticks; it's updated once per bar in `BuildIndicatorSnapshot`
- The `if (_broker is NamedPipeBrokerAdapter pipeAdapter) pipeAdapter.OnClientConnected = ResetState;` line in `ExecuteAsync` — wire `OnClientConnected` in `NetMQBrokerAdapter` directly, or expose a hook via `IBrokerAdapter`

For `OnClientConnected` / `ResetState`: add `Action? OnConnected { get; set; }` to `NetMQBrokerAdapter` (similar to existing `NamedPipeBrokerAdapter.OnClientConnected`) and wire it in `EngineWorker` the same way:
```csharp
if (_broker is NetMQBrokerAdapter mqAdapter)
    mqAdapter.OnConnected = ResetState;
if (_broker is NamedPipeBrokerAdapter pipeAdapter)
    pipeAdapter.OnClientConnected = ResetState;
```

---

## Phase 5 — Structured Observability

The objective: an agent (or human) running `dotnet test --filter Pipeline` and watching the log can trace the complete lifecycle without guessing.

### 5a. Log line protocol

All structured log lines use `|`-separated fields for easy grep/filter:

| Level | Pattern | When |
|---|---|---|
| Info | `BAR_EVAL\|{sym}\|{tf}\|{close}\|bars={n}\|total={n}` | Before evaluating strategies on each bar |
| Debug | `EVAL\|{strategy}\|{sym}\|{result}\|have={n}\|need={n}` | Per strategy evaluation result |
| Info | `SIGNAL\|{strategy}\|{sym}\|{dir}\|sl={sl}\|tp={tp}` | When a signal fires |
| Info | `SIGNAL_REASON\|{strategy}\|{reason}` | Signal human-readable reason |
| Info | `ORDER\|{strategy}\|{orderId}\|{dir}\|lots={n}\|entry={price}` | After order dispatched |
| Info | `EXEC\|{orderId}\|{state}\|fill={price}\|lots={n}` | On execution event processed |
| Info | `ACCOUNT\|balance={b}\|equity={e}\|dd={dd%}` | When account update consumed |
| Info | `NETMQ\|CONNECTED\|dataEndpoint={ep}\|commandEndpoint={ep}` | When cBot identity captured |
| Warn | `NETMQ\|CMD_DROPPED\|{reason}` | If command can't be sent |

`EXEC` and `ACCOUNT` log lines are currently missing. Add them in `ProcessTicksAsync` where executions and account updates are consumed:

```csharp
// In the execution drain loop:
_positionTracker.OnExecution(execEvent, _strategies);
_logger.LogInformation("EXEC|{OrderId}|{State}|fill={Fill}|lots={Lots}",
    execEvent.OrderId, execEvent.NewState,
    execEvent.FillPrice?.Value.ToString("F5") ?? "none",
    execEvent.FilledLots);

// In HandleAccountUpdate:
_logger.LogInformation("ACCOUNT|balance={Balance:F2}|equity={Equity:F2}|dd={DD:P1}",
    update.Balance, update.Equity, riskState.DailyDrawdownUsed);
```

### 5b. SIGNAL|NO classification

Currently `SIGNAL|NO` is a single line with no reason. Add specific sub-codes so you can grep for why signals aren't firing:

```csharp
// In ProcessBarsAsync (replacing the current EVAL|NO logging):
if (intent is null)
{
    // Strategy.Evaluate should set a LastEvalReason or similar if you want detail.
    // For now, at least distinguish NO from NEED_BARS:
    _logger.LogDebug("EVAL|{Strategy}|{Symbol}|NO_SIGNAL", strategy.Id, bar.Symbol.Value);
    continue;
}
```

If strategies don't expose a reason yet, at minimum change `SIGNAL|NO||{Ind}` to `EVAL|{strategy}|{sym}|NO_SIGNAL` so it's clearly categorized.

---

## Phase 6 — Dead Code Removal

Remove these files entirely after Phase 1 is complete and tests pass:

**cBot project** (no longer needed):
- `src/TradingEngine.Adapters.CTrader/PipeClient.cs`
- `src/TradingEngine.Adapters.CTrader/PipeMessage.cs`
- `src/TradingEngine.Adapters.CTrader/MessageSerializer.cs`
- `src/TradingEngine.Adapters.CTrader/TickPublisher.cs`
- `src/TradingEngine.Adapters.CTrader/BarPublisher.cs`
- `src/TradingEngine.Adapters.CTrader/AccountUpdatePublisher.cs`
- `src/TradingEngine.Adapters.CTrader/ExecutionEventPublisher.cs`
- `src/TradingEngine.Adapters.CTrader/OrderCommandHandler.cs`

**Infrastructure** (superseded by NetMQBrokerAdapter):
- `src/TradingEngine.Infrastructure/Adapters/NamedPipeBrokerAdapter.cs` — delete after confirming no other references. If the Aspire path still needs it, keep but mark `[Obsolete]`.

**CTraderRunner** (no longer pipe-based):
- Remove `using System.IO.Pipes` from `BacktestRunner.cs`
- Remove `--PipeName=` from `BuildArgs`

**Tests**:
- `tests/TradingEngine.Tests.Simulation/Pipeline/PipeConnectivityTest.cs` — replace with `NetMQBridgeTest` (Phase 7)

**DECISIONS.md** — add entries:
- D-new: NetMQ adopted as cBot↔engine transport. Named pipes abandoned — .NET managed socket API intercepted by ctrader-cli sandbox; NetMQ native P/Invoke not intercepted.
- D-new: Strategy evaluation moved to bar close. Tick loop handles fills/risk only.
- D-new: `bars.BarClosed` event used in cBot (not `OnBar()` override) to capture full history including warmup bars.

---

## Phase 7 — Tests

### 7a. NetMQBridgeTest (primary agent loop — no ctrader-cli needed)

**File**: `tests/TradingEngine.Tests.Simulation/Pipeline/NetMQBridgeTest.cs`

Add `NetMQ` NuGet to `TradingEngine.Tests.Simulation.csproj`.

```csharp
[Trait("Category", "NetMQ")]
public sealed class NetMQBridgeTest
{
    [Fact(Timeout = 20_000)]
    public async Task EngineReceivesBarAndTickOverNetMQ()
    {
        // Start engine in Live (NetMQ) mode
        var dataPort    = 15555;
        var commandPort = 15556;
        var workDir     = Path.Combine(Path.GetTempPath(), "shamshir-mq", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        var logPath  = Path.Combine(workDir, "engine.log");
        var solRoot  = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var projPath = Path.Combine(solRoot, "src", "TradingEngine.Host", "TradingEngine.Host.csproj");

        using var engine = Process.Start(new ProcessStartInfo("dotnet",
            $"run --project \"{projPath}\" --no-build")
        {
            UseShellExecute = false, CreateNoWindow = true,
            Environment =
            {
                ["Engine__Mode"]                         = "Live",
                ["Engine__Broker__Type"]                 = "NetMQ",
                ["Engine__Broker__NetMQ__DataPort"]      = dataPort.ToString(),
                ["Engine__Broker__NetMQ__CommandPort"]   = commandPort.ToString(),
                ["SERILOG_FILE_PATH"]                    = logPath,
            },
        })!;

        try
        {
            // Wait for engine ROUTER to bind (probe with DEALER)
            var ready = false;
            for (var i = 0; i < 40 && !ready; i++)
            {
                await Task.Delay(250);
                try
                {
                    using var probe = new DealerSocket();
                    probe.Connect($"tcp://127.0.0.1:{commandPort}");
                    probe.Disconnect($"tcp://127.0.0.1:{commandPort}");
                    ready = true;
                }
                catch { }
            }
            ready.Should().BeTrue("engine ROUTER should bind within 10s");

            // Act as fake cBot: bind PUB on dataPort, connect DEALER on commandPort
            using var pub    = new PublisherSocket();
            using var dealer = new DealerSocket();
            pub.Bind($"tcp://*:{dataPort}");
            dealer.Connect($"tcp://127.0.0.1:{commandPort}");
            await Task.Delay(500); // slow joiner

            // Send hello to register identity
            dealer.SendFrame("""{"type":"hello"}""");

            // Publish one Bar and one Tick
            var barJson = """{"type":"bar","symbol":"EURUSD","period":"H1","openTime":"2024-01-15T00:00:00Z","open":1.09000,"high":1.09500,"low":1.08800,"close":1.09200,"volume":1000}""";
            pub.SendMoreFrame("bar").SendFrame(barJson);

            var tickJson = """{"type":"tick","symbol":"EURUSD","bid":1.09200,"ask":1.09202,"time":"2024-01-15T01:00:00Z"}""";
            pub.SendMoreFrame("tick").SendFrame(tickJson);

            await Task.Delay(2000); // let engine process
        }
        finally
        {
            if (!engine.HasExited) engine.Kill(entireProcessTree: true);
            await engine.WaitForExitAsync(CancellationToken.None);
        }

        // Assert on engine log
        await Task.Delay(500);
        var lines = File.Exists(logPath) ? await File.ReadAllLinesAsync(logPath) : [];
        Console.WriteLine($"[TEST] Log lines: {lines.Length}");
        foreach (var l in lines.Where(l => l.Contains("BAR") || l.Contains("TICK") || l.Contains("NETMQ") || l.Contains("CONNECTED")))
            Console.WriteLine($"  {l}");

        lines.Should().Contain(l => l.Contains("NETMQ") && l.Contains("CONNECTED"),
            "engine should log identity capture");
        lines.Should().Contain(l => l.Contains("BAR_EVAL") && l.Contains("EURUSD"),
            "engine should log BAR_EVAL after receiving bar");
    }
}
```

Run command:
```
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "Category=NetMQ"
```
Expected: 1 passed, <20s, no cTrader credentials needed.

**If this fails**: check whether the engine process started (log file exists?), whether the ROUTER port bound (probe succeeds?), whether the SUB received data (check CONNECTED log line). Each failure mode has a distinct symptom in the log.

### 7b. FullBacktestPipelineTest — fast variant

Add 3-day variant for agent iteration. Mark 3-month as Slow:

```csharp
[Trait("Category", "Pipeline")]
[Fact(Timeout = 90_000)]
public async Task EurUsdH1_ThreeDays_VerifiesNetMQDataFlow()
{
    // Same structure as ThreeMonth but:
    // Start = new DateTime(2024, 1, 15), End = new DateTime(2024, 1, 18)
    // Assertions: netmqConnected > 0, barLines > 0, tickLines > 0
    // Then: signalYes.Should().NotBeEmpty() (3 days of H1 gives ~45 bars — borderline for warmup)
    // If strategy needs 55 bars, use a 2-week range instead
}

[Trait("Category", "Slow")]        // excluded from default dotnet test
[Trait("Category", "Pipeline")]
[Fact(Timeout = 600_000)]
public async Task EurUsdH1_ThreeMonth_GeneratesAtLeastOneTrade() { ... }
```

Assertions must be ordered (ordered preconditions):
```csharp
var netmqConnected = allLines.Where(l => l.Contains("NETMQ") && l.Contains("CONNECTED")).ToList();
var barLines       = allLines.Where(l => l.Contains("BAR_EVAL")).ToList();
var tickLines      = allLines.Where(l => l.Contains("TICK|")).ToList();
var signalYes      = allLines.Where(l => l.Contains("SIGNAL|") && !l.Contains("SIGNAL_REASON") && l.Contains("|YES|") || l.Contains("SIGNAL|")).ToList();
// actually SIGNAL lines look like: SIGNAL|strategy|sym|dir|sl=...|tp=...
// grep for "SIGNAL|" that is NOT "SIGNAL_REASON|"

if (!netmqConnected.Any())
    Assert.Fail($"cBot never connected via NetMQ. Check CBOT| lines in stdout:\n{cBotStdout}");
if (!barLines.Any())
    Assert.Fail($"No bars received. NetMQ connected but no data. tickLines={tickLines.Count}");
barLines.Count.Should().BeGreaterThan(0);
signalYes.Should().NotBeEmpty("strategies should signal over the test period");
```

---

## Verification Sequence

Run these in order. Each must pass before the next.

**Step 1 — No regressions (existing tests)**
```
dotnet test tests\TradingEngine.Tests.Unit --no-build
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "Category!=NetMQ&Category!=Pipeline"
```
All existing simulation scenarios (DrawdownScenarios, TrendBreakoutScenarios, MultiStrategyScenarios) must pass.

**Step 2 — NetMQBridgeTest (primary agent iteration loop)**
```
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "Category=NetMQ"
```
Expected: 1 passed, <20s. Proves the engine's NetMQBrokerAdapter works without ctrader-cli.

**Step 3 — Fast pipeline test (requires cTrader credentials + built .algo)**
```
set CTrader__CtId=seankiaa
set CTrader__PwdFile=C:\Users\shahi\Documents\ctrader.pwd
set CTrader__Account=5834367
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "ThreeDays"
```
Expected: 1 passed, <90s. Log shows `NETMQ|CONNECTED`, `BAR_EVAL` lines > 0.

**Step 4 — Full 3-month test**
```
dotnet test tests\TradingEngine.Tests.Simulation --no-build --filter "ThreeMonth"
```
Expected: 1 passed. `SIGNAL|` lines > 0.

---

## Critical Files

| File | Action |
|---|---|
| `src/TradingEngine.Adapters.CTrader/TradingEngineCBot.cs` | Full rewrite (Phase 1) |
| `src/TradingEngine.Adapters.CTrader/TradingEngine.Adapters.CTrader.csproj` | Add NetMQ, remove Newtonsoft |
| `src/TradingEngine.Adapters.CTrader/Pipe*.cs` + `*Publisher.cs` + `OrderCommandHandler.cs` | Delete all 8 files |
| `src/TradingEngine.Infrastructure/Adapters/NetMQBrokerAdapter.cs` | New file (Phase 2) |
| `src/TradingEngine.Infrastructure/TradingEngine.Infrastructure.csproj` | Add NetMQ |
| `src/TradingEngine.Host/EngineWorker.cs` | ProcessBarsAsync gains eval; ProcessTicksAsync loses eval |
| `src/TradingEngine.Host/Program.cs` | Wire NetMQBrokerAdapter |
| `src/TradingEngine.CTraderRunner/BacktestRunner.cs` | Port-based readiness probe; remove pipe refs |
| `src/TradingEngine.CTraderRunner/TradingEngine.CTraderRunner.csproj` | Add NetMQ |
| `tests/TradingEngine.Tests.Simulation/Pipeline/NetMQBridgeTest.cs` | New file (Phase 7) |
| `tests/TradingEngine.Tests.Simulation/Pipeline/FullBacktestPipelineTest.cs` | ThreeDays variant + ordered assertions |
| `tests/TradingEngine.Tests.Simulation/TradingEngine.Tests.Simulation.csproj` | Add NetMQ |
| `DECISIONS.md` | 3 new entries (transport, bar-close eval, BarClosed event) |

---

## Notes for Implementing Agent

1. **NetMQ version**: Use `NetMQ` version `4.*`. Do not use `AsyncIO.NetMQ` or `clrzmq4`. NetMQ 4 is the maintained pure-.NET binding.

2. **cBot LangVersion is 10**, not 6 (check the csproj). Modern C# features are available. `System.Text.Json` is available on net6.0.

3. **Build the `.algo` before running pipeline tests**:
   ```
   dotnet build src\TradingEngine.Adapters.CTrader -c Debug
   ```
   The `.algo` is in `bin/Debug/net6.0/src.algo`. If you change `[Parameter]` names, run `dotnet clean` first — cTrader.Automate caches a `.cbotset` file that will reject renamed parameters.

4. **Do not use `bars.BarOpened`** — it fires with placeholder zero data during history load. `bars.BarClosed` is correct. The cBot should filter out bars where `Open == 0` as a defensive check.

5. **Slow joiner**: The cBot must sleep ~600ms after binding PUB before sending data. The engine SUB needs time to complete the TCP handshake. Already accounted for in Phase 1 with `Thread.Sleep(600)`.

6. **NetMQConfig.Cleanup(false)** in `OnStop` is needed to prevent port conflicts on subsequent backtest runs within the same ctrader-cli session.

7. **EngineTestHarness** does not use `EngineWorker`. Moving strategy evaluation to bar-close in `ProcessBarsAsync` does not affect harness tests. The harness evaluates on each of the 4 synthetic ticks (its own loop). This is fine — harness tests are for strategy correctness, not engine routing.

8. **Simulated path** (`EngineMode.Backtest` + `SimulatedBrokerAdapter`): `DataFeedService` feeds bars and ticks. Bars arrive via `BarStream`, triggering `ProcessBarsAsync` → strategy eval. Ticks arrive via `TickStream`, triggering `ProcessTicksAsync` → `sim.OnTickReceived` → fills. This is now clean: no double-evaluation, correct ordering.

9. **Remove `await Task.Yield()` from tick loop** — it was a workaround for task scheduler starvation when the tick loop was doing heavy work (strategy eval). With the lightweight tick loop, it's unnecessary overhead.

10. **`_reusableIndicatorDict`** was shared between tick and bar processors but is now only written in `ProcessBarsAsync` and read in `ProcessBarsAsync` (same method). It's no longer accessed from `ProcessTicksAsync`. If you want to be safe, make it a local variable inside `ProcessBarsAsync` instead of a field. The field is fine as long as both usages are in the same async method body (no concurrent access).

11. Write `docs/ITERATION-8-HANDOVER.md` at the end with: what was completed, what files changed, any remaining issues, and the verified test results.
