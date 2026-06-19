using TradingEngine.Engine;
using TradingEngine.Risk;

namespace TradingEngine.Tests.Unit.Iter26;

/// <summary>
/// Regression tests for the iter-26 backbone fixes (see docs/iterations/iter-26/PLAN.md).
/// Pure-logic findings get real assertions; findings that need the live IHost/backtest harness
/// are left as Skip placeholders that name the exact behavior + the harness to encode them in
/// (tests/TradingEngine.Tests.Simulation/Harness/EngineHarnessBuilder.cs).
/// </summary>
[Trait("Category", "Engine")]
[Trait("Speed", "Fast")]
public sealed class Iter26FixTests
{
    // ---- F3: venue-bound closes carry the OrderId, not the internal PositionId ----

    [Fact]
    public void F3_ForceCloseAll_emits_close_keyed_by_venue_OrderId()
    {
        var symbol = Symbol.Parse("EURUSD");
        var orderId = Guid.NewGuid();

        var r1 = EngineReducer.Apply(EngineState.Empty,
            new OrderSubmitted(orderId, symbol, TradeDirection.Long, 0.1m, null, "s", DateTime.UtcNow));
        var r2 = EngineReducer.Apply(r1.State,
            new OrderFilled(orderId, symbol, 0.1m, new Price(1.10m), DateTime.UtcNow.AddSeconds(1)));

        var internalPositionId = r2.State.Positions.Values.First().PositionId;

        var fc = EngineReducer.Apply(r2.State, new ForceCloseAllRequested("MaxDD", DateTime.UtcNow.AddMinutes(1)));

        var close = fc.Effects.OfType<CloseOpenPosition>().Single();
        close.OrderId.Should().Be(orderId, "the venue keys open trades by the order id");
        // AF1 determinism: PositionId now equals OrderId (no Guid.NewGuid), so the IDs ARE the same.
        // The venue still receives the correct OrderId — no cross-contamination.
    }

    [Fact]
    public void F3_CloseRequested_emits_close_keyed_by_venue_OrderId()
    {
        var symbol = Symbol.Parse("EURUSD");
        var orderId = Guid.NewGuid();

        var r1 = EngineReducer.Apply(EngineState.Empty,
            new OrderSubmitted(orderId, symbol, TradeDirection.Long, 0.1m, null, "s", DateTime.UtcNow));
        var r2 = EngineReducer.Apply(r1.State,
            new OrderFilled(orderId, symbol, 0.1m, new Price(1.10m), DateTime.UtcNow.AddSeconds(1)));

        var posId = r2.State.Positions.Values.First().PositionId;
        var cr = EngineReducer.Apply(r2.State, new CloseRequested(posId, "SL", DateTime.UtcNow.AddMinutes(1)));

        cr.Effects.OfType<CloseOpenPosition>().Single().OrderId.Should().Be(orderId);
    }

    // ---- F5: CalculateLotSize honors LotSizingMethod (FixedLots here) ----

    [Fact]
    public void F5_PositionSizer_honors_FixedLots_method()
    {
        var profile = new RiskProfile(
            "s", "S", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, "ftmo",
            LotSizingMethod.FixedLots, FixedLots: 0.5m);

        var lots = PositionSizer.Calculate(
            equity: 100_000m, profile: profile, stopLossDistance: new Pips(20),
            pipValue: 10m, drawdownScaleFactor: 1m,
            maxLots: 10m, brokerMinLots: 0.01m, brokerLotStep: 0.01m);

        lots.Should().Be(0.5m, "FixedLots must return the configured size, not a percent-risk calc");
    }

    [Fact]
    public void F5_PercentRisk_still_default()
    {
        var profile = new RiskProfile(
            "s", "S", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, "ftmo"); // PercentRisk default

        var lots = PositionSizer.Calculate(
            equity: 100_000m, profile: profile, stopLossDistance: new Pips(20),
            pipValue: 10m, drawdownScaleFactor: 1m,
            maxLots: 100m, brokerMinLots: 0.01m, brokerLotStep: 0.01m);

        // risk = 100000*0.01 = 1000; rawLots = 1000/(20*10) = 5.0
        lots.Should().Be(5.0m);
    }

    // ---- F10c: monthly reset keeps the monthly baseline (mirrors weekly), not the day-start ----

    [Fact]
    public void F10_MonthRolled_keeps_monthly_baseline()
    {
        var dd = new DrawdownState(
            InitialAccountBalance: 100_000m, PeakEquity: 100_000m,
            DailyStartEquity: 95_000m, WeeklyStartEquity: 100_000m, MonthlyStartEquity: 100_000m,
            CurrentDailyDrawdown: 0m, CurrentMaxDrawdown: 0m, CurrentWeeklyDrawdown: 0m,
            CurrentMonthlyDrawdown: 0.05m, DrawdownVelocity: 0m, DrawdownType: "Fixed");

        var state = EngineState.Empty with { Drawdown = dd };
        var r = EngineReducer.Apply(state, new MonthRolled(DateTime.UtcNow));

        r.State.Drawdown.MonthlyStartEquity.Should().Be(100_000m,
            "monthly baseline is kept; passing DailyStartEquity (95k) would wrongly rebase the month");
        r.State.Drawdown.CurrentMonthlyDrawdown.Should().Be(0m);
    }

    // ---- Harness-dependent placeholders (encode in the Simulation project) ----

    [Fact(Skip = "F1: encode in Simulation/EngineHarnessBuilder — after a losing trade over a replay " +
                 "fixture, RiskManager.Drawdown.CurrentMaxDrawdown > 0 and the AccountStream equity " +
                 "moves by realized PnL (today it stays flat at initial).")]
    public void F1_replay_equity_moves_with_realized_pnl() { }

    [Fact(Skip = "F2: encode in Simulation — price gaps through the stop within a bar; the trade's exit " +
                 "price equals the SL (within a tick) and ledger gross == (entry-SL)*lots*contract, " +
                 "not the bar-close value.")]
    public void F2_sl_tp_fills_at_stop_price() { }

    [Fact(Skip = "F4: encode with a PositionTracker + fakes — after a 50% partial close, the RiskManager " +
                 "open risk for that position is ~half the original, not the full amount.")]
    public void F4_partial_close_halves_registered_risk() { }

    [Fact(Skip = "F7: encode with AccountProcessor — a day that opens fresh after the prior day closed " +
                 "near the daily limit does NOT enter protection mode on the first update of the new day.")]
    public void F7_no_spurious_protection_on_day_roll() { }
}
