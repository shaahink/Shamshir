using TradingEngine.Engine;
using KernelCore = TradingEngine.Engine.Kernel;

namespace TradingEngine.Tests.Unit.Kernel;

/// <summary>
/// iter-39 Stream D — adversarial rule-pressure tests. Prove daily loss limits, max-DD terminality,
/// and swap computation all work correctly under stress.
/// Builds on the <see cref="GFx"/> fixtures (iter-37 Phase G).
/// </summary>
[Trait("Category", "Kernel")]
[Trait("Speed", "Fast")]
public sealed class RulePressureTests
{
    private static readonly DateTime Day1 = new(2026, 1, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 1, 8, 22, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan ResetUtc = TimeSpan.FromHours(22);

    private static readonly ConstraintSet Rules = GFx.Constraints(dailyBase: DailyDdBase.DailyStart);

    private static SymbolInfo Eurusd(decimal commissionPerSide = 0, decimal swapLong = 0, decimal swapShort = 0)
        => new(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m,
            "USD", commissionPerSide, swapLong, swapShort, "Wednesday");

    private static KernelCore Kernel() => new(new KernelConfig(
        Rules, GFx.Profile, GFx.Sizing, _ => GFx.SymInfo, _ => [], Seed: 42));

    // D2: daily loss limit halts trading for the day, resumes next day (multi-bar scenario).
    [Fact]
    public void DailyLossLimit_HaltsMidDay_ResumesNextDay()
    {
        var kernel = Kernel();

        var breached = kernel.Decide(GFx.State(), new EquityObserved(10_000m, 9_450m, 0m, Day1)).State;
        breached.Protection.InProtectionMode.Should().BeTrue("5.5% DD breaches the 5% daily limit");
        breached.Protection.Cause.Should().Be(ProtectionCause.DailyDrawdown);

        var nextDay = kernel.Decide(breached, new DayRolled(Day2)).State;
        nextDay.Protection.InProtectionMode.Should().BeFalse("daily protection clears on next day");
        nextDay.Drawdown.CurrentDailyDrawdown.Should().Be(0m, "daily DD re-bases to new day's opening equity");
    }

    // D3: max-DD breach is terminal — does NOT clear on daily roll.
    [Fact]
    public void MaxDrawdownBreach_IsTerminal_DoesNotClearOnDailyRoll()
    {
        var kernel = Kernel();

        var maxBreached = GFx.State() with
        {
            Protection = new ProtectionState(true, ProtectionCause.MaxDrawdown, "total-loss", "AccountReset", null),
        };
        var afterRoll = kernel.Decide(maxBreached, new DayRolled(Day2)).State;
        afterRoll.Protection.InProtectionMode.Should().BeTrue("max-DD breach is terminal");
        afterRoll.Protection.Cause.Should().Be(ProtectionCause.MaxDrawdown);
    }

    // D4: skipped — governor ConsecutiveLosses requires a full trade lifecycle with
    // PublishTradeClosed, not just CloseRequested events. Deferred to a future harness test.
    [Fact(Skip = "D4: governor streak tracking requires full trade lifecycle — needs kernel-loop harness with FakeVenue")]
    public void Governor_ConsecutiveLosses_Tracked() { }

    // D5: weekend triple-swap detection logic.
    [Fact]
    public void WednesdayOvernight_IsTripleSwap()
    {
        var nights = TradingEngine.Services.Helpers.TradeCostCalculator.CountNightsHeld(
            new DateTime(2026, 6, 17, 10, 0, 0, DateTimeKind.Utc),  // Wednesday
            new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc),  // Thursday
            "Wednesday",
            ResetUtc);

        nights.Should().Be(3, "Wednesday overnight is triple-swap day");
    }

    [Fact]
    public void WeekendHolding_ChargesOnlyTheFridayRollover()
    {
        var nights = TradingEngine.Services.Helpers.TradeCostCalculator.CountNightsHeld(
            new DateTime(2026, 6, 19, 10, 0, 0, DateTimeKind.Utc),  // Friday
            new DateTime(2026, 6, 22, 12, 0, 0, DateTimeKind.Utc),  // Monday
            "Wednesday",
            ResetUtc);

        // P4.4 (F45): this asserted 3 ("crosses Fri, Sat, Sun"). It DOES cross three rollovers — but the
        // market is shut for two of them and no broker finances a position over a closed market. That is
        // precisely why Wednesday is billed triple. Measured: cTrader charged a Fri→Mon EURUSD long
        // 35.90 EUR = ONE night at its declared -2.445 pips (three would have been ~107).
        nights.Should().Be(1, "Sat/Sun rollovers are not charged — only Friday's");
    }

    // G1: verify TripleSwapWeekday correctly triples swap on Wednesday.
    [Fact]
    public void TripleSwap_WednesdayPosition_ChargedTripleNights()
    {
        var sym = Eurusd(swapShort: -1.5m);
        var costs = TradingEngine.Services.Helpers.TradeCostCalculator.Compute(
            TradeDirection.Short, new Price(1.1000m), new Price(1.1010m), lots: 1m,
            sym, (_, _) => 1m,
            new DateTime(2026, 6, 17, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc),
            ResetUtc);

        costs.NightsHeld.Should().Be(3, "Wed→Thu: 1 night × triple = 3 nights charged");
        // P4.4 (F45): the rate is PIPS, signed as a P&L adjustment — negative = the trader PAYS. Money =
        // nights × ratePips × lots × pipValue (100_000 × 0.0001 × 1 = 10/lot). This asserted +4.5, which
        // both dropped the pip value AND flipped a broker charge into a credit.
        costs.Swap.Should().Be(-45m, "3 nights × -1.5 pips × 1 lot × 10/pip = -45 (a cost)");
    }
}
