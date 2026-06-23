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
    public void WeekendHolding_CrossesThreeRollovers()
    {
        var nights = TradingEngine.Services.Helpers.TradeCostCalculator.CountNightsHeld(
            new DateTime(2026, 6, 19, 10, 0, 0, DateTimeKind.Utc),  // Friday
            new DateTime(2026, 6, 22, 12, 0, 0, DateTimeKind.Utc),  // Monday
            "Wednesday",
            ResetUtc);

        nights.Should().Be(3, "Friday→Monday crosses Fri, Sat, Sun = 3 nights");
    }
}
