using Microsoft.Extensions.Logging.Abstractions;

namespace TradingEngine.Tests.Integration.AdapterTests;

public sealed class BacktestReplayAdapterTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<Bar> MakeBars(int count, decimal startClose = 1.1000m)
    {
        var bars = new List<Bar>(count);
        for (var i = 0; i < count; i++)
        {
            var close = startClose - i * 0.0010m;
            bars.Add(new Bar(
                Eurusd, Timeframe.H1,
                T0.AddHours(i),
                close + 0.0005m,
                close + 0.0010m,
                close - 0.0010m,
                close,
                1000));
        }
        return bars;
    }

    private static BacktestReplayAdapter MakeAdapter(IReadOnlyList<Bar> bars)
    {
        var repo = Substitute.For<IBarRepository>();
        repo.GetAsync(Arg.Any<Symbol>(), Arg.Any<Timeframe>(),
                      Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(bars));

        var symbolInfo = new SymbolInfo(Eurusd, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);
        var symbolRegistry = Substitute.For<ISymbolInfoRegistry>();
        symbolRegistry.Get(Eurusd).Returns(symbolInfo);

        return new BacktestReplayAdapter(
            repo, Eurusd, Timeframe.H1, T0, T0.AddDays(1),
            10_000m, symbolRegistry, (_, _) => 1.0m,
            NullLogger<BacktestReplayAdapter>.Instance);
    }

    [Fact(Timeout = 15_000)]
    public async Task AllBars_DeliveredWithoutDataLoss()
    {
        const int barCount = 3_000;
        var adapter = MakeAdapter(MakeBars(barCount));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await adapter.ConnectAsync(cts.Token);

        var received = 0;
        await foreach (var _ in adapter.BarStream.ReadAllAsync(cts.Token))
            received++;

        received.Should().Be(barCount,
            $"all {barCount} bars must arrive; got {received} — data loss detected (BUG-02)");

        await adapter.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task SubmitOrder_ReceivesInstantFillWithPrice()
    {
        var bars = MakeBars(10);
        var adapter = MakeAdapter(bars);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await adapter.ConnectAsync(cts.Token);

        var firstBar = await adapter.BarStream.ReadAsync(cts.Token);

        var intent = new TradeIntent(
            Eurusd, TradeDirection.Long, OrderType.Market, null,
            new Price(firstBar.Close - 0.0050m),
            new Price(firstBar.Close + 0.0050m),
            "test-strategy", "standard", "test", firstBar.OpenTimeUtc);
        var request = new OrderRequest(intent, 0.01m, Eurusd, TradeDirection.Long, OrderType.Market, null);

        var orderId = await adapter.SubmitOrderAsync(request, cts.Token);

        var execEvent = await adapter.ExecutionStream.ReadAsync(cts.Token);

        execEvent.OrderId.Should().Be(orderId);
        execEvent.NewState.Should().Be(OrderState.Filled);
        execEvent.FillPrice.Should().NotBeNull("BUG-01: fill price must not be null");
        execEvent.FillPrice!.Value.Value.Should().BeGreaterThan(0);
        execEvent.FilledLots.Should().Be(0.01m);

        await adapter.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task ClosePosition_SendsFillPriceNotNull()
    {
        var bars = MakeBars(5);
        var adapter = MakeAdapter(bars);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        await adapter.ConnectAsync(cts.Token);

        await adapter.BarStream.ReadAsync(cts.Token);

        var fakePositionId = Guid.NewGuid();
        await adapter.ClosePositionAsync(fakePositionId, cts.Token);

        var execEvent = await adapter.ExecutionStream.ReadAsync(cts.Token);

        execEvent.OrderId.Should().Be(fakePositionId);
        execEvent.NewState.Should().Be(OrderState.Filled);
        execEvent.FillPrice.Should().NotBeNull("BUG-03: ClosePositionAsync must send a fill price");
        execEvent.FillPrice!.Value.Value.Should().BeGreaterThan(0);

        await adapter.DisposeAsync();
    }
}
