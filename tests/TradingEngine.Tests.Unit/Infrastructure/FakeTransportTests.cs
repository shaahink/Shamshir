using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace TradingEngine.Tests.Unit.Infrastructure;

[Trait("Category", "Infrastructure")]
public sealed class FakeTransportTests
{
    private sealed class FakeMessageTransport : IMessageTransport
    {
        private readonly Channel<(string, string)> _subChannel =
            Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });
        private readonly Channel<(byte[], string)> _routerChannel =
            Channel.CreateBounded<(byte[], string)>(new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });

        public readonly List<(byte[] Identity, string Json)> SentMessages = new();

        public ChannelReader<(string Topic, string Json)> SubMessages => _subChannel.Reader;
        public ChannelReader<(byte[] Identity, string Json)> RouterMessages => _routerChannel.Reader;

        public ChannelWriter<(string, string)> SubWriter => _subChannel.Writer;
        public ChannelWriter<(byte[], string)> RouterWriter => _routerChannel.Writer;

        public bool IsConnected { get; set; } = true;
        public Action? OnConnected { get; set; }

        public void Send(byte[] identity, string json)
        {
            SentMessages.Add((identity, json));
        }

        public Task ConnectAsync(CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken ct)
        {
            _subChannel.Writer.TryComplete();
            _routerChannel.Writer.TryComplete();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SubmitOrder_ThenCompleteBar_ProducesBarDoneJson()
    {
        var transport = new FakeMessageTransport();
        var logger = Substitute.For<ILogger<CTraderBrokerAdapter>>();
        var adapter = new CTraderBrokerAdapter(transport, logger);

        await adapter.ConnectAsync(CancellationToken.None);

        await transport.RouterWriter.WriteAsync((new byte[] { 1, 2, 3 }, """{"type":"hello"}"""));
        await Task.Delay(100);

        var intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
            new Price(1.0821m), null, "test", "standard", "", DateTime.UtcNow);
        var orderReq = new OrderRequest(intent, 0.1m, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);

        var orderId = await adapter.SubmitOrderAsync(orderReq, CancellationToken.None);
        orderId.Should().NotBeEmpty();

        await adapter.CompleteBarAsync(1, CancellationToken.None);

        transport.SentMessages.Should().HaveCount(2);
        var (_, json) = transport.SentMessages[1];

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().Should().Be("bar_done");
        root.GetProperty("seq").GetInt64().Should().Be(1);
        root.GetProperty("v").GetInt32().Should().Be(1);

        var commands = root.GetProperty("commands");
        commands.ValueKind.Should().Be(JsonValueKind.Array);
        commands.GetArrayLength().Should().Be(1);

        var cmd = commands[0];
        cmd.GetProperty("type").GetString().Should().Be("submit_order");
        cmd.GetProperty("symbol").GetString().Should().Be("EURUSD");
        cmd.GetProperty("lots").GetDouble().Should().Be(0.1);
        cmd.GetProperty("slPrice").GetDouble().Should().Be(1.0821);
        cmd.GetProperty("tpPrice").GetDouble().Should().Be(0.0);

        await adapter.DisconnectAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InboundExecMessage_SurfacesOnExecutionStream()
    {
        var transport = new FakeMessageTransport();
        var logger = Substitute.For<ILogger<CTraderBrokerAdapter>>();
        var adapter = new CTraderBrokerAdapter(transport, logger);

        await adapter.ConnectAsync(CancellationToken.None);

        var orderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var execJson = $$"""
            {"type":"exec","clientOrderId":"{{orderId}}","state":"Filled","fillPrice":1.0850,"filledLots":0.1,"simTime":"2024-01-01T10:00:00Z","grossProfit":28.0,"netProfit":27.5,"commission":0.3,"swap":0.2}
            """;

        await transport.RouterWriter.WriteAsync((new byte[] { 1, 2, 3 }, execJson));

        await Task.Delay(200);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exec = await adapter.ExecutionStream.ReadAsync(cts.Token);

        exec.OrderId.Should().Be(orderId);
        exec.NewState.Should().Be(OrderState.Filled);
        exec.FillPrice!.Value.Value.Should().Be(1.0850m);
        exec.FilledLots.Should().Be(0.1m);
        exec.GrossProfit.Should().Be(28.0m);
        exec.NetProfit.Should().Be(27.5m);
        exec.Commission.Should().Be(0.3m);
        exec.Swap.Should().Be(0.2m);

        await adapter.DisconnectAsync(CancellationToken.None);
    }

    // ── V1/V2 — startup/reconnect reconciliation from the cBot hello snapshot ──
    [Fact]
    public async Task HelloWithPositions_ReconcilesAndSurfacesViaGetAccountState()
    {
        var transport = new FakeMessageTransport();
        var adapter = new CTraderBrokerAdapter(transport, Substitute.For<ILogger<CTraderBrokerAdapter>>());

        AccountState? reconciled = null;
        adapter.RegisterReconcileHandler(s => reconciled = s);

        await adapter.ConnectAsync(CancellationToken.None);

        var orderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var hello = $$"""
            {"type":"hello","v":1,"account":{"balance":10000.0,"equity":10250.0},
             "positions":[{"clientOrderId":"{{orderId}}","symbol":"EURUSD","direction":"Long",
                           "lots":0.25,"entryPrice":1.1000,"stopLoss":1.0950,"takeProfit":1.1100,"venuePositionId":98765}]}
            """;
        await transport.RouterWriter.WriteAsync((new byte[] { 1, 2, 3 }, hello));
        await Task.Delay(150);

        // V1 — GetAccountStateAsync now returns the venue snapshot, not (0,0,[]).
        var state = await adapter.GetAccountStateAsync(CancellationToken.None);
        state.Balance.Should().Be(10000m);
        state.Equity.Should().Be(10250m);
        state.OpenPositions.Should().HaveCount(1);

        var p = state.OpenPositions[0];
        p.PositionId.Should().Be(orderId, "the engine clientOrderId Guid keys the position (V2)");
        p.Symbol.Should().Be(Symbol.Parse("EURUSD"));
        p.Direction.Should().Be(TradeDirection.Long);
        p.Lots.Should().Be(0.25m);
        p.EntryPrice.Value.Should().Be(1.1000m);
        p.CurrentStopLoss.Value.Should().Be(1.0950m);
        p.TakeProfit!.Value.Value.Should().Be(1.1100m);

        // reconcile callback fired with the same snapshot
        reconciled.Should().NotBeNull();
        reconciled!.OpenPositions.Should().HaveCount(1);

        await adapter.DisconnectAsync(CancellationToken.None);
    }

    // ── V3 — venue-confirmed SL modification routes to the writeback hook, not the exec stream ──
    [Fact]
    public async Task ModifyConfirmation_FiresStopModifiedHandler_AndNotExecutionStream()
    {
        var transport = new FakeMessageTransport();
        var adapter = new CTraderBrokerAdapter(transport, Substitute.For<ILogger<CTraderBrokerAdapter>>());

        (Guid Order, Price Sl, Price? Tp)? confirmed = null;
        adapter.RegisterStopModifiedHandler((o, sl, tp) => confirmed = (o, sl, tp));

        await adapter.ConnectAsync(CancellationToken.None);

        var orderId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var modify = $$"""
            {"type":"exec","kind":"modify","clientOrderId":"{{orderId}}","state":"Filled","slPrice":1.0980,"tpPrice":1.1100}
            """;
        await transport.RouterWriter.WriteAsync((new byte[] { 1, 2, 3 }, modify));
        await Task.Delay(150);

        confirmed.Should().NotBeNull();
        confirmed!.Value.Order.Should().Be(orderId);
        confirmed.Value.Sl.Value.Should().Be(1.0980m);
        confirmed.Value.Tp!.Value.Value.Should().Be(1.1100m);

        // a modify must NOT be treated as a fill on the execution stream
        adapter.ExecutionStream.TryRead(out _).Should().BeFalse();

        await adapter.DisconnectAsync(CancellationToken.None);
    }

    // ── V5 — a command buffered for a bar that fails to send on disconnect is re-queued and
    //         delivered on the next bar_done after reconnect, instead of being dropped. ──
    [Fact]
    public async Task BufferedCommand_NotDroppedOnDisconnect_DeliveredAfterReconnect()
    {
        var transport = new FakeMessageTransport();
        var adapter = new CTraderBrokerAdapter(transport, Substitute.For<ILogger<CTraderBrokerAdapter>>());

        await adapter.ConnectAsync(CancellationToken.None);
        await transport.RouterWriter.WriteAsync((new byte[] { 1, 2, 3 }, """{"type":"hello"}"""));
        await Task.Delay(100);

        var intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
            new Price(1.0821m), null, "test", "standard", "", DateTime.UtcNow);
        var orderReq = new OrderRequest(intent, 0.1m, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);
        await adapter.SubmitOrderAsync(orderReq, CancellationToken.None);  // buffered for the bar

        var sentBeforeDisconnect = transport.SentMessages.Count;

        // Disconnect, then complete the bar — the command must NOT be sent, but MUST be retained.
        transport.IsConnected = false;
        await adapter.CompleteBarAsync(1, CancellationToken.None);
        transport.SentMessages.Count.Should().Be(sentBeforeDisconnect, "nothing is sent while disconnected");

        // Reconnect: a fresh hello re-flushes the re-queued command into the next bar buffer.
        transport.IsConnected = true;
        await transport.RouterWriter.WriteAsync((new byte[] { 1, 2, 3 }, """{"type":"hello"}"""));
        await Task.Delay(100);

        await adapter.CompleteBarAsync(2, CancellationToken.None);

        // The last bar_done must carry the re-queued submit_order.
        var (_, json) = transport.SentMessages[^1];
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("type").GetString().Should().Be("bar_done");
        var commands = root.GetProperty("commands");
        commands.GetArrayLength().Should().Be(1, "the buffered order survived the disconnect and rode the next bar_done");
        commands[0].GetProperty("type").GetString().Should().Be("submit_order");

        await adapter.DisconnectAsync(CancellationToken.None);
    }

    // ── 31-C2: limit offset intent produces a limit order frame with populated limitPrice and expiryBars ──
    [Fact]
    public async Task LimitOffsetIntent_ProducesLimitOrderFrame()
    {
        var transport = new FakeMessageTransport();
        var adapter = new CTraderBrokerAdapter(transport, Substitute.For<ILogger<CTraderBrokerAdapter>>());

        await adapter.ConnectAsync(CancellationToken.None);
        await transport.RouterWriter.WriteAsync((new byte[] { 1, 2, 3 }, """{"type":"hello"}"""));
        await Task.Delay(100);

        var intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Short, OrderType.Limit,
            new Price(1.0750m), new Price(1.0800m), new Price(1.0700m),
            "mean-reversion", "standard", "limit-offset-5", DateTime.UtcNow)
        {
            Entry = new OrderEntryOptions
            {
                Method = OrderEntryMethod.LimitOffset,
                LimitOffsetPips = 5,
                LimitOrderExpiryBars = 3,
                MaxSlippagePips = 2.0,
            },
        };
        var orderReq = new OrderRequest(intent, 0.1m, intent.Symbol, intent.Direction, OrderType.Limit, intent.LimitPrice);

        await adapter.SubmitOrderAsync(orderReq, CancellationToken.None);
        await adapter.CompleteBarAsync(1, CancellationToken.None);

        transport.SentMessages.Should().HaveCount(2, "hello ack + bar_done");
        var (_, json) = transport.SentMessages[1];
        using var doc = JsonDocument.Parse(json);
        var commands = doc.RootElement.GetProperty("commands");
        commands.GetArrayLength().Should().Be(1);

        var cmd = commands[0];
        cmd.GetProperty("type").GetString().Should().Be("submit_order");
        cmd.GetProperty("orderType").GetString().Should().Be("Limit");
        cmd.GetProperty("limitPrice").GetDouble().Should().BeGreaterThan(0, "limitPrice must be populated for Limit orders");
        cmd.GetProperty("expiryBars").GetInt32().Should().Be(3);
        cmd.GetProperty("symbol").GetString().Should().Be("EURUSD");
        cmd.GetProperty("direction").GetString().Should().Be("Short");
        cmd.GetProperty("lots").GetDouble().Should().Be(0.1);
        cmd.GetProperty("slPrice").GetDouble().Should().Be(1.0800);
        cmd.GetProperty("tpPrice").GetDouble().Should().Be(1.0700);

        await adapter.DisconnectAsync(CancellationToken.None);
    }
}
