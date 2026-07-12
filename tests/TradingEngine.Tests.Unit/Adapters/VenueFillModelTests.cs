using TradingEngine.Infrastructure.Adapters;

namespace TradingEngine.Tests.Unit.Adapters;

/// <summary>
/// P4.3 (F43). Pins <see cref="VenueFillModel"/> against SIX REAL cTrader FILLS, taken from live
/// compare-both runs and reproduced here tick-exact from the M1 bars the venue replayed.
///
/// These are not invented fixtures. Each case below is a row from an actual cTrader backtest
/// (RunIds d64d9488 / 81729685) paired with the M1 bar that produced it, straight out of marketdata.db.
/// They are the evidence that the venue fills a resting order on the FIRST of its four synthetic
/// O/H/L/C ticks to breach the level — never at the level itself.
///
/// Why this file exists: for five sessions both replay venues modelled every resting order as "fills at
/// its own price". That is not what any venue does, and it is why tape long-stops came out ~5 pips
/// optimistic and tape limits ~0.2 pips pessimistic against cTrader. The bug survived that long because
/// the guarding test asserted the ASSUMED contract instead of a measured one. If someone "simplifies"
/// VenueFillModel back to "fill at the level", every case here must fail loudly.
///
/// Full derivation: docs/iterations/iter-alpha-loop/PARITY-TRUTH-4.md §2.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class VenueFillModelTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly Symbol Xauusd = Symbol.Parse("XAUUSD");
    private static readonly DateTime T = new(2026, 6, 1, 13, 12, 0, DateTimeKind.Utc);

    private static Bar M1(Symbol s, decimal o, decimal h, decimal l, decimal c) => new(s, Timeframe.M1, T, o, h, l, c, 10);

    /// <summary>Shifts a bid bar to the ask side, exactly as the adapters do (SpreadConvention.AskBar).</summary>
    private static Bar Ask(Bar bid, decimal spread) => new(
        bid.Symbol, bid.Timeframe, bid.OpenTimeUtc,
        bid.Open + spread, bid.High + spread, bid.Low + spread, bid.Close + spread, bid.Volume);

    // ---- The six measured venue fills -------------------------------------------------------------
    //
    // EURUSD spread = 1.0 pip = 0.0001 (the --spread both legs were given).
    // XAUUSD spread = 0.01.

    [Fact]
    public void LongStopLoss_FillsAtTheM1Low_NotAtTheStop()
    {
        // cTrader run d64d9488, trade 2: Long, SL 1.163295, closed 2026-06-01 13:13 at 1.16283.
        // Triggering M1 bid bar (13:12). A long exits by SELLING at the BID — the raw bar.
        // Ticks: O=1.16356 (above the stop) → H=1.16358 (above) → L=1.16283 (BREACH) → fill there.
        var bid = M1(Eurusd, 1.16356m, 1.16358m, 1.16283m, 1.16291m);

        VenueFillModel.FirstBreachingTick(bid, level: 1.163295m, fallsToLevel: true)
            .Should().Be(1.16283m, "cTrader filled this stop 4.65 pips THROUGH it, at the M1 low — the first tick to breach");
    }

    [Fact]
    public void ShortStopLoss_FillsAtTheM1AskHigh_NotAtTheStopPlusAnotherSpread()
    {
        // cTrader run d64d9488, trade 1: Short, SL 1.16254, closed 2026-05-28 12:26 at 1.16255.
        // Triggering M1 bid bar (12:25) O=1.1622 H=1.16245. A short exits by BUYING at the ASK.
        // Ask bar: O=1.1623 (below the stop) → H=1.16255 (BREACH) → fill there.
        var ask = Ask(M1(Eurusd, 1.1622m, 1.16245m, 1.16219m, 1.16241m), 0.0001m);

        VenueFillModel.FirstBreachingTick(ask, level: 1.16254m, fallsToLevel: false)
            .Should().Be(1.16255m, "the stop is already an ASK-side level — the old code added the spread to it a SECOND time (1.16264)");
    }

    [Fact]
    public void ShortStopLoss_SecondMeasuredFill_LandsExactlyOnTheStop()
    {
        // cTrader run d64d9488, trade 3: Short, SL 1.16350, closed 2026-06-04 10:49 at 1.16350.
        // Triggering M1 bid bar (10:48) H=1.1634 → ask high 1.1635 = the stop exactly. Same rule, but here
        // the breaching tick happens to land ON the level — which is why one trade alone could never have
        // discriminated between "fills at the level" and "fills at the breaching tick".
        var ask = Ask(M1(Eurusd, 1.16321m, 1.1634m, 1.1632m, 1.16323m), 0.0001m);

        VenueFillModel.FirstBreachingTick(ask, level: 1.16350m, fallsToLevel: false)
            .Should().Be(1.16350m);
    }

    [Fact]
    public void SellLimitEntry_FillsAtTheM1BidHigh_BetterThanTheLimit()
    {
        // cTrader run d64d9488, trade 1 ENTRY: sell limit 1.15973, opened 2026-05-28 05:34 at 1.15975.
        // Triggering M1 bid bar (05:33) O=1.15963 (below the limit) → H=1.15975 (BREACH) → fill there,
        // 2 ticks BETTER than the limit. A sell-to-open executes at the raw BID.
        var bid = M1(Eurusd, 1.15963m, 1.15975m, 1.15963m, 1.15973m);

        VenueFillModel.FirstBreachingTick(bid, level: 1.15973m, fallsToLevel: false)
            .Should().Be(1.15975m, "a limit fills at-or-BETTER than its price — the P2 'never better' contract was wrong");
    }

    [Fact]
    public void ShortTakeProfit_FillsAtTheM1AskLow_BetterThanTheTarget()
    {
        // cTrader run 81729685, XAUUSD Short: TP 3973.665, closed 2026-06-30 01:07 at 3964.80.
        // Triggering M1 bid bar (01:06) O=3974.88 L=3964.79. A short exits by BUYING at the ASK.
        // Ask bar: O=3974.89 (above the target) → L=3964.80 (BREACH) → fill there.
        var ask = Ask(M1(Xauusd, 3974.88m, 3974.88m, 3964.79m, 3967.39m), 0.01m);

        VenueFillModel.FirstBreachingTick(ask, level: 3973.665m, fallsToLevel: true)
            .Should().Be(3964.80m, "the target is reached by price FALLING for a short — the fill is the ask low");
    }

    [Fact]
    public void ShortTakeProfit_OnAFastBar_FillsFarThroughTheTarget()
    {
        // cTrader run 81729685, XAUUSD Short: TP 3966.303, closed 2026-06-30 01:08 at 3938.47.
        // Triggering M1 bid bar (01:07) O=3967.14 L=3938.46 — a 29-point M1 candle. Ask low = 3938.47.
        // The venue's M1 tick synthesis overshoots the target by 27.8 points here; "fill at the target"
        // would have understated this exit by that much.
        var ask = Ask(M1(Xauusd, 3967.14m, 3967.56m, 3938.46m, 3945.28m), 0.01m);

        VenueFillModel.FirstBreachingTick(ask, level: 3966.303m, fallsToLevel: true)
            .Should().Be(3938.47m);
    }

    // ---- The gap case: the OPEN is itself the first breaching tick ---------------------------------

    [Fact]
    public void WhenTheBarOpensThroughTheLevel_TheOpenIsTheFill()
    {
        // Price gapped past the level before the bar began, so the very first tick (the open) breaches it.
        // This is the old "gap-through" special case — now just the ordinary first-breaching-tick rule.
        var bid = M1(Eurusd, 1.1600m, 1.1605m, 1.1590m, 1.1602m);

        VenueFillModel.FirstBreachingTick(bid, level: 1.1620m, fallsToLevel: true)
            .Should().Be(1.1600m, "the open already sits below the level — no better tick exists");

        VenueFillModel.FirstBreachingTick(bid, level: 1.1580m, fallsToLevel: false)
            .Should().Be(1.1600m, "the open already sits above the level");
    }
}
