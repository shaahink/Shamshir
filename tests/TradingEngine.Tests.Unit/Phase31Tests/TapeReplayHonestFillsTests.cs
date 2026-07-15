using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Unit.Phase31Tests;

/// <summary>
/// P0.3 (D4): when finer (M1) exit bars are available, a market order must queue and fill at the NEXT
/// fine bar's open, not fill instantly at submit time (the old behavior let a signal fill at the very
/// close of the bar that produced it — before that bar, or any subsequent one, had actually played out).
/// `HonestFills=false` preserves the old behavior for A/B. Drives the adapter directly via
/// SubmitOrderAsync/OnBarObserved (dual-resolution mode: decisionTf=H1, exitTf=M1), matching the pattern
/// in TapeReplaySpreadConventionTests.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class TapeReplayHonestFillsTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc);
    private const decimal Spread = 0.0002m;

    private static TapeReplayAdapter MakeAdapter(bool honestFills, IReadOnlyList<Bar> m1Bars)
    {
        var store = Substitute.For<IMarketDataStore>();
        // The feed task also reads decision-TF (H1) bars in the background (unused here — tests drive
        // OnBarObserved directly) — stub it too so that background read doesn't NRE on a null default.
        store.ReadBarsAsync(Eurusd, Timeframe.H1, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bar>>([]));
        store.ReadBarsAsync(Eurusd, Timeframe.M1, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(m1Bars));

        var symbolInfo = new SymbolInfo(Eurusd, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, Spread);
        var registry = Substitute.For<ISymbolInfoRegistry>();
        registry.Get(Eurusd).Returns(symbolInfo);

        return new TapeReplayAdapter(
            store, Eurusd, Timeframe.H1, Timeframe.M1, T0, T0.AddHours(3),
            10_000m, registry, (_, _) => 1.0m,
            NullLogger<TapeReplayAdapter>.Instance, honestFills);
    }

    private static Bar H1(DateTime t, decimal o, decimal h, decimal l, decimal c) => new(Eurusd, Timeframe.H1, t, o, h, l, c, 1000);
    private static Bar M1(DateTime t, decimal o, decimal h, decimal l, decimal c) => new(Eurusd, Timeframe.M1, t, o, h, l, c, 10);

    private static List<ExecutionEvent> Drain(TapeReplayAdapter a)
    {
        var list = new List<ExecutionEvent>();
        while (a.ExecutionStream.TryRead(out var e)) list.Add(e);
        return list;
    }

    private static async Task<(Guid orderId, TapeReplayAdapter adapter)> ConnectAndSubmit(
        bool honestFills, IReadOnlyList<Bar> m1Bars, TradeDirection dir, CancellationToken ct)
    {
        var adapter = MakeAdapter(honestFills, m1Bars);
        await adapter.ConnectAsync(ct); // loads the M1 exit bars up front (dual-resolution)
        adapter.OnBarObserved(H1(T0, 1.1000m, 1.1010m, 1.0990m, 1.1000m)); // decision bar @10:00

        // SL/TP far enough from the fine bars' ranges (~1.0995-1.1010) that neither direction's entry
        // fill gets immediately re-closed by ProcessSlTpHits in the same OnBarObserved call — these tests
        // are about the ENTRY fill, not exit behaviour.
        var (sl, tp) = dir == TradeDirection.Long
            ? (new Price(1.0900m), new Price(1.1100m))
            : (new Price(1.1100m), new Price(1.0900m));
        var intent = new TradeIntent(Eurusd, dir, OrderType.Market, null, sl, tp, "test", "standard", "", T0);
        var orderId = await adapter.SubmitOrderAsync(
            new OrderRequest(intent, 1.0m, Eurusd, dir, OrderType.Market, null), ct);
        return (orderId, adapter);
    }

    // A fine bar at exactly T0 falls inside the FIRST decision bar's own window (windowEnd = T0+1h), so
    // it is consumed by the first OnBarObserved call in ConnectAndSubmit — BEFORE the order is even
    // submitted. Keeps _exitBars.Count > 0 (so the honest-fills gate stays active) without prematurely
    // filling anything the "does not fill at submit" tests assert against.
    private static readonly Bar NoOpFineBarAtT0 = M1(T0, 1.0999m, 1.1001m, 1.0998m, 1.1000m);

    // A fine bar timed to arrive AFTER the order is submitted: it falls inside the SECOND decision bar's
    // window (11:00–12:00), so it is only consumed once the test explicitly calls OnBarObserved a second
    // time — simulating "the next fine bar after the signal".
    private static readonly Bar NextFineBar = M1(T0.AddHours(1).AddMinutes(1), 1.1001m, 1.1005m, 1.0998m, 1.1002m);

    [Fact]
    public async Task HonestFillsOn_MarketOrder_DoesNotFillAtSubmit()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var m1Bars = new[] { NoOpFineBarAtT0, NextFineBar };

        var (_, adapter) = await ConnectAndSubmit(true, m1Bars, TradeDirection.Long, cts.Token);

        Drain(adapter).Should().BeEmpty("a market order must queue, not fill instantly, when HonestFills is on and finer bars exist");
    }

    [Fact]
    public async Task HonestFillsOn_MarketOrder_FillsAtNextFineBarOpen()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var m1Bars = new[] { NoOpFineBarAtT0, NextFineBar };

        var (orderId, adapter) = await ConnectAndSubmit(true, m1Bars, TradeDirection.Long, cts.Token);
        Drain(adapter);

        // Advance to the decision bar whose window contains NextFineBar (11:01 < 12:00).
        adapter.OnBarObserved(H1(T0.AddHours(1), 1.1000m, 1.1010m, 1.0995m, 1.1005m));

        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId);
        fill.FillPrice!.Value.Value.Should().Be(1.1003m, "long fills at the fine bar's open(1.1001) + full spread(0.0002)");
    }

    [Fact]
    public async Task HonestFillsOn_ShortMarketOrder_FillsAtNextFineBarOpen_Unadjusted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var m1Bars = new[] { NoOpFineBarAtT0, NextFineBar };

        var (orderId, adapter) = await ConnectAndSubmit(true, m1Bars, TradeDirection.Short, cts.Token);
        Drain(adapter);

        adapter.OnBarObserved(H1(T0.AddHours(1), 1.1000m, 1.1010m, 1.0995m, 1.1005m));

        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId);
        fill.FillPrice!.Value.Value.Should().Be(1.1001m, "short fills at the raw fine-bar open — no spread charged");
    }

    [Fact]
    public async Task HonestFillsOff_PreservesOldBehavior_FillsInstantlyAtSubmit()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var m1Bars = new[] { M1(T0.AddMinutes(1), 1.1001m, 1.1005m, 1.0998m, 1.1002m) };

        var (orderId, adapter) = await ConnectAndSubmit(false, m1Bars, TradeDirection.Long, cts.Token);

        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId);
        fill.FillPrice!.Value.Value.Should().Be(1.1002m, "old behavior: instant fill at decision-bar close(1.1000) + spread(0.0002)");
    }

    [Fact]
    public async Task PendingMarketOrder_OnLastBarOfRun_StillFillsAtDisconnect()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Keeps _exitBars.Count > 0 (honest-fills gate active) but nothing arrives AFTER the order is
        // submitted — simulating the run ending right after the signal, with no next fine bar to fill it.
        var m1Bars = new[] { NoOpFineBarAtT0 };

        var (orderId, adapter) = await ConnectAndSubmit(true, m1Bars, TradeDirection.Long, cts.Token);
        Drain(adapter).Should().BeEmpty("still queued — no fine bar has arrived yet");

        await adapter.DisconnectAsync(cts.Token);

        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId, "a pending order must still fill at disconnect, or trade counts silently differ from the A/B baseline");
    }
}
