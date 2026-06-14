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

        public bool IsConnected => true;
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
}
