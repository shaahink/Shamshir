using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Unit.Phase31Tests;

/// <summary>
/// P2 (docs/reference/RESTING-ORDER-CONTRACT.md). Guards the resting-order contract: the fill price the
/// venue actually produces (P4.3/F43 — the first O/H/L/C tick to breach the level, NOT the level itself;
/// see <c>VenueFillModel</c>), and — the regression this file exists to prevent (F30) — expiry counted in
/// DECISION-timeframe bars on BOTH venues, never fine (exit-resolution) bars. The cBot's OnBarClosed only
/// ever sees decision bars, so a tape adapter that decrements BarsRemaining per fine bar (the default M1
/// exit resolution) burns a 3-bar expiry in ~3 minutes instead of ~3 decision bars — a silent fill/no-fill
/// divergence indistinguishable from a signal bug.
///
/// P2's original claim — "entry price is identical to cTrader BY CONSTRUCTION, because both fill at exactly
/// the named limit" — was never measured against cTrader, and was wrong in both halves: the venue does not
/// fill at the named price, and the tape did not match it. Fill-price parity is now an EMPIRICAL result,
/// pinned against real venue fills in <see cref="Adapters.VenueFillModelTests"/>, not a construction argument.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class RestingOrderContractTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc);
    private const decimal Spread = 0.0002m;

    private static TapeReplayAdapter MakeAdapter(Timeframe decisionTf, Timeframe exitTf, IReadOnlyList<Bar> exitBars)
    {
        var store = Substitute.For<IMarketDataStore>();
        store.ReadBarsAsync(Eurusd, decisionTf, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bar>>([]));
        store.ReadBarsAsync(Eurusd, exitTf, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(exitBars));

        var symbolInfo = new SymbolInfo(Eurusd, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, Spread);
        var registry = Substitute.For<ISymbolInfoRegistry>();
        registry.Get(Eurusd).Returns(symbolInfo);

        return new TapeReplayAdapter(
            store, Eurusd, decisionTf, exitTf, T0, T0.AddDays(2),
            10_000m, registry, (_, _) => 1.0m,
            NullLogger<TapeReplayAdapter>.Instance);
    }

    private static Bar H4(DateTime t, decimal o, decimal h, decimal l, decimal c) => new(Eurusd, Timeframe.H4, t, o, h, l, c, 1000);
    private static Bar M1(DateTime t, decimal o, decimal h, decimal l, decimal c) => new(Eurusd, Timeframe.M1, t, o, h, l, c, 10);

    private static List<ExecutionEvent> Drain(TapeReplayAdapter a)
    {
        var list = new List<ExecutionEvent>();
        while (a.ExecutionStream.TryRead(out var e)) list.Add(e);
        return list;
    }

    private static async Task<Guid> SubmitLimit(TapeReplayAdapter a, TradeDirection dir, Price limit, Price sl, Price? tp, int expiryBars)
    {
        var entry = new OrderEntryOptions { LimitOrderExpiryBars = expiryBars };
        var intent = new TradeIntent(Eurusd, dir, OrderType.Limit, limit, sl, tp, "test", "standard", "", T0) { Entry = entry };
        return await a.SubmitOrderAsync(new OrderRequest(intent, 1.0m, Eurusd, dir, OrderType.Limit, limit), CancellationToken.None);
    }

    // --- Touch rule + fill price (single-resolution — decisionTf == exitTf) ---
    //
    // P4.3 (F43) REVERSED THE CONTRACT THESE TWO TESTS ORIGINALLY ASSERTED. P2 asserted "a limit fills at
    // exactly the named price, never a better one" — reasoned from first principles, never measured against
    // the venue. It is false. cTrader replays M1 as four synthetic ticks (O/H/L/C); a resting order fills on
    // the first tick to BREACH its level, and with no tick at the level itself the fill lands on the bar's
    // EXTREME — at-or-better than the limit. Measured tick-exact on the real venue: a sell limit at 1.15973
    // filled at 1.15975 (the M1 bid high). See PARITY-TRUTH-4.md §2 and VenueFillModel.

    [Fact]
    public async Task BuyLimit_FillsAtTheFirstBreachingTick_WhichIsBetterThanTheLimit()
    {
        var adapter = MakeAdapter(Timeframe.H4, Timeframe.H4, []);
        adapter.OnBarObserved(H4(T0, 1.1000m, 1.1005m, 1.0995m, 1.1000m));
        var limit = new Price(1.0980m);
        var orderId = await SubmitLimit(adapter, TradeDirection.Long, limit, new Price(1.0900m), new Price(1.1100m), expiryBars: 3);
        Drain(adapter).Should().BeEmpty("resting limit must not fill at submit");

        // Bid bar trades down through the limit. A buy executes at the ASK (bid + 0.0002 spread), so the
        // ask bar is O=1.0997 H=1.1000 L=1.0972 C=1.0987. The open (1.0997) has not breached the 1.0980
        // limit, so the first breaching tick is the ask LOW.
        adapter.OnBarObserved(H4(T0.AddHours(4), 1.0995m, 1.0998m, 1.0970m, 1.0985m));

        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId);
        fill.FillPrice!.Value.Value.Should().Be(1.0972m,
            "the venue has no tick at the limit — it fills on the first O/H/L/C tick to breach it, here the ask low");
    }

    [Fact]
    public async Task SellLimit_FillsAtTheFirstBreachingTick_WhichIsBetterThanTheLimit()
    {
        var adapter = MakeAdapter(Timeframe.H4, Timeframe.H4, []);
        adapter.OnBarObserved(H4(T0, 1.1000m, 1.1005m, 1.0995m, 1.1000m));
        var limit = new Price(1.1030m);
        var orderId = await SubmitLimit(adapter, TradeDirection.Short, limit, new Price(1.1100m), new Price(1.0900m), expiryBars: 3);

        // A sell-to-open executes at the raw BID. The open (1.1005) has not breached the 1.1030 limit, so
        // the first breaching tick is the bid HIGH (1.1040) — a better sell than the limit named.
        adapter.OnBarObserved(H4(T0.AddHours(4), 1.1005m, 1.1040m, 1.1000m, 1.1010m));

        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId);
        fill.FillPrice!.Value.Value.Should().Be(1.1040m,
            "the venue has no tick at the limit — it fills on the first O/H/L/C tick to breach it, here the bid high");
    }

    // --- F30 regression guard: expiry must count DECISION bars, not fine bars ---

    [Fact]
    public async Task DualResolution_LimitOrder_ExpiryCountsDecisionBars_NotFineBars()
    {
        // decisionTf=H4, exitTf=M1 — the actual default (BacktestOrchestrator.cs ExitTimeframe ?? "M1").
        // Each H4 window contains 240 M1 bars. A naive per-fine-bar decrement would exhaust a 3-bar
        // expiry within the first 3 minutes of the FIRST decision window. The contract requires it to
        // survive across 3 full DECISION bars instead.
        var exitBars = new List<Bar>();
        for (var t = T0; t < T0.AddHours(16); t = t.AddMinutes(1))
            exitBars.Add(M1(t, 1.0999m, 1.1001m, 1.0998m, 1.1000m)); // flat — never touches the limit below

        var adapter = MakeAdapter(Timeframe.H4, Timeframe.M1, exitBars);
        await adapter.ConnectAsync(CancellationToken.None);
        adapter.OnBarObserved(H4(T0, 1.1000m, 1.1005m, 1.0995m, 1.1000m)); // decision bar 1 @10:00, consumes its M1 bars — no order yet, so no decrement matters

        var limit = new Price(1.0500m); // far away — never reached by the flat fixture bars
        var orderId = await SubmitLimit(adapter, TradeDirection.Long, limit, new Price(1.0400m), new Price(1.1600m), expiryBars: 3);
        Drain(adapter).Should().BeEmpty();

        // Decision bar 2 (~240 more M1 bars processed in this single call) — 1st decrement, 2 lives left.
        adapter.OnBarObserved(H4(T0.AddHours(4), 1.1000m, 1.1005m, 1.0995m, 1.1000m));
        Drain(adapter).Should().BeEmpty(
            "only 1 decision bar has elapsed since submit; a fine-bar-granular decrement would have " +
            "already expired this order within the first few M1 bars of decision bar 2 — that is bug F30");

        // Decision bar 3 — 2nd decrement, 1 life left. Still not expired.
        adapter.OnBarObserved(H4(T0.AddHours(8), 1.1000m, 1.1005m, 1.0995m, 1.1000m));
        Drain(adapter).Should().BeEmpty("2 decision bars elapsed of a 3-bar expiry — not expired yet");

        // Decision bar 4 — 3rd decrement, 0 lives left, expires now.
        adapter.OnBarObserved(H4(T0.AddHours(12), 1.1000m, 1.1005m, 1.0995m, 1.1000m));
        var evt = Drain(adapter).Single();
        evt.OrderId.Should().Be(orderId);
        evt.RejectionReason.Should().Be("ENTRY_EXPIRED", "expiry fires after 3 full decision bars, matching the cBot's once-per-decision-bar cadence");
    }

    [Fact]
    public async Task DualResolution_LimitOrder_StillFillsMidWindow_TouchDetectionUnaffectedByExpiryFix()
    {
        // The F30 fix must not weaken TOUCH detection — a limit reached by a fine bar within a later
        // decision window must still fill immediately on that fine bar (fine-bar SL/TP/touch fidelity
        // is a separate, intentional feature — RESTING-ORDER-CONTRACT.md §4/§6) — not deferred until
        // the whole decision bar closes, and not blocked by the (fixed) expiry countdown.
        var exitBars = new[] { M1(T0.AddHours(4).AddMinutes(1), 1.0990m, 1.0995m, 1.0975m, 1.0985m) }; // trades down through 1.0980

        var adapter = MakeAdapter(Timeframe.H4, Timeframe.M1, exitBars);
        await adapter.ConnectAsync(CancellationToken.None);
        adapter.OnBarObserved(H4(T0, 1.1000m, 1.1005m, 1.0995m, 1.1000m)); // decision bar 1 — no fine bars in this window

        var limit = new Price(1.0980m);
        var orderId = await SubmitLimit(adapter, TradeDirection.Long, limit, new Price(1.0900m), new Price(1.1100m), expiryBars: 3);
        Drain(adapter).Should().BeEmpty();

        // Decision bar 2's window contains the touching fine bar — should fill immediately on it, well
        // before the 3-bar expiry would fire (decision bar 4).
        adapter.OnBarObserved(H4(T0.AddHours(4), 1.1000m, 1.1005m, 1.0995m, 1.1000m));

        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId);
        // Fine bid bar L=1.0975 → ask low 1.0977, the first tick to breach the 1.0980 buy limit (F43).
        fill.FillPrice!.Value.Value.Should().Be(1.0977m, "touch detection still runs every fine bar — only the expiry countdown changed");
    }
}
