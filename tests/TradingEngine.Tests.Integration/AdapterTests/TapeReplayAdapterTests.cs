using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Tests.Integration.MarketData;

namespace TradingEngine.Tests.Integration.AdapterTests;

/// <summary>
/// iter-marketdata-tape P3 — the fake venue. Proves (a) it feeds decision bars from the canonical store and
/// (b) DUAL-RESOLUTION exits change the outcome vs decision-bar OHLC: with both SL and TP inside one H1 bar,
/// decision-bar detection is pessimistic ("SL"), but replaying the m1 path — which touches TP first — closes
/// at TP. This is the intrabar/long-shadow fidelity the owner cares about.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class TapeReplayAdapterTests : IDisposable
{
    private readonly TempMarketData _md = new();
    private static readonly Symbol Eur = Symbol.Parse("EURUSD");
    private static readonly DateTime T10 = new(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc);

    // Entry 1.1000, SL 1.0980 (−20p), TP 1.1020 (+20p). H1 bar1 spans BOTH; m1 path hits TP before SL.
    private const decimal Entry = 1.1000m, Sl = 1.0980m, Tp = 1.1020m;

    private static Bar H1(DateTime t, decimal o, decimal h, decimal l, decimal c) => new(Eur, Timeframe.H1, t, o, h, l, c, 100);
    private static Bar M1(DateTime t, decimal o, decimal h, decimal l, decimal c) => new(Eur, Timeframe.M1, t, o, h, l, c, 10);

    private TapeReplayAdapter MakeAdapter(Timeframe exitTf)
    {
        var symbolInfo = new SymbolInfo(Eur, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);
        var registry = Substitute.For<ISymbolInfoRegistry>();
        registry.Get(Eur).Returns(symbolInfo);
        return new TapeReplayAdapter(
            _md.Store, Eur, Timeframe.H1, exitTf, T10, T10.AddHours(3),
            10_000m, registry, (_, _) => 1.0m, NullLogger<TapeReplayAdapter>.Instance);
    }

    private async Task SeedAsync()
    {
        // Decision H1 bars: entry bar @10:00, exposure bar @11:00 whose H1 range contains BOTH SL and TP.
        await _md.Store.WriteBarsAsync("test", new[]
        {
            H1(T10, 1.1000m, 1.1005m, 1.0995m, 1.1000m),
            H1(T10.AddHours(1), 1.1010m, 1.1025m, 1.0975m, 1.1015m),
        });
        // Finer m1 path inside 11:00–12:00: first bar hits TP (not SL), later bar hits SL.
        await _md.Store.WriteBarsAsync("test", new[]
        {
            M1(T10.AddHours(1), 1.1010m, 1.1025m, 1.1005m, 1.1015m),          // 11:00 → touches TP only
            M1(T10.AddHours(1).AddMinutes(1), 1.1015m, 1.1016m, 1.0975m, 1.0980m), // 11:01 → touches SL
        });
    }

    private async Task<string?> RunAndGetCloseReason(Timeframe exitTf)
    {
        await SeedAsync();
        var adapter = MakeAdapter(exitTf);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await adapter.ConnectAsync(cts.Token);

        var bar0 = await adapter.BarStream.ReadAsync(cts.Token);   // 10:00
        adapter.OnBarObserved(bar0);

        var posId = Guid.NewGuid();
        var intent = new TradeIntent(Eur, TradeDirection.Long, OrderType.Market, null,
            new Price(Sl), new Price(Tp), "test-strategy", "standard", "test", bar0.OpenTimeUtc);
        await adapter.SubmitOrderAsync(
            new OrderRequest(intent, 0.01m, Eur, TradeDirection.Long, OrderType.Market, null, posId), cts.Token);

        var bar1 = await adapter.BarStream.ReadAsync(cts.Token);   // 11:00
        adapter.OnBarObserved(bar1);

        await adapter.DisconnectAsync(cts.Token);
        var execs = new List<ExecutionEvent>();
        await foreach (var e in adapter.ExecutionStream.ReadAllAsync())
            execs.Add(e);
        await adapter.DisposeAsync();

        return execs.SingleOrDefault(e => e.CloseReason != null)?.CloseReason;
    }

    [Fact(Timeout = 15_000)]
    public async Task DualResolution_closes_at_TP_when_m1_hits_TP_before_SL()
    {
        var reason = await RunAndGetCloseReason(Timeframe.M1);
        reason.Should().Be("TP", "the m1 path reaches TP before SL, which single-bar OHLC cannot see");
    }

    [Fact(Timeout = 15_000)]
    public async Task DecisionResolution_is_pessimistic_and_closes_at_SL()
    {
        // exitTf == decisionTf ⇒ no finer data ⇒ single-resolution detection on the H1 bar (SL wins the tie).
        var reason = await RunAndGetCloseReason(Timeframe.H1);
        reason.Should().Be("SL", "with both levels inside one H1 bar, decision-bar detection is pessimistic");
    }

    [Fact(Timeout = 15_000)]
    public async Task Feeds_decision_bars_from_the_store()
    {
        await SeedAsync();
        var adapter = MakeAdapter(Timeframe.M1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await adapter.ConnectAsync(cts.Token);

        var received = new List<Bar>();
        await foreach (var b in adapter.BarStream.ReadAllAsync(cts.Token))
            received.Add(b);

        received.Should().HaveCount(2);
        received.Should().OnlyContain(b => b.Timeframe == Timeframe.H1, "the strategy decides on the decision timeframe");
        await adapter.DisposeAsync();
    }

    public void Dispose() => _md.Dispose();
}
