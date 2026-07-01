using TradingEngine.Engine;
using KernelCore = TradingEngine.Engine.Kernel;

namespace TradingEngine.Tests.Unit.Kernel;

/// <summary>
/// iter-37 Phase F — FTMO rule pressure through the kernel decision core. These drive a sequence of
/// kernel events (equity breach → roll) and assert at the PRODUCTION pre-trade gate that trading actually
/// halts and resumes correctly — the daily-vs-overall distinction (F1 resumes, F2 doesn't), which is the
/// real-money behaviour C4/H7 are about. Reuses the <see cref="GFx"/> fixtures.
/// </summary>
[Trait("Category", "Kernel")]
[Trait("Speed", "Fast")]
public sealed class FtmoPressureTests
{
    private static readonly DateTime Day1 = new(2026, 1, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 1, 8, 22, 0, 0, DateTimeKind.Utc);

    // Daily DD measured from the day's own start (FTMO-style), so day 2 re-bases its loss budget.
    private static readonly ConstraintSet Rules = GFx.Constraints(dailyBase: DailyDdBase.DailyStart);

    private static KernelCore Kernel() => new(new KernelConfig(
        Rules, GFx.Profile, GFx.Sizing, _ => GFx.SymInfo, _ => [], Seed: 42));

    private static string? GateReject(EngineState s) =>
        PreTradeGate.Evaluate(s, GFx.Proposal(), Rules, GFx.Profile, GFx.Sizing, GFx.SymInfo, []).RejectReason;

    [Fact] // F1 — daily loss limit halts trading for the day, resumes the next (depends on G0 roll + reset)
    public void Ftmo_DailyLossLimit_HaltsTradingForTheDay_ResumesNextDay()
    {
        var kernel = Kernel();

        // Day 1: a 6% equity drop trips the daily-DD breach watchdog → protection.
        var breached = kernel.Decide(GFx.State(), new EquityObserved(10_000m, 9_400m, 0m, Day1)).State;
        breached.Protection.InProtectionMode.Should().BeTrue("a 6% daily loss breaches the daily limit");
        GateReject(breached).Should().Be("PROTECTION_MODE_ACTIVE", "no new entries for the rest of the day");

        // Day 2 roll: protection clears AND the daily floor re-bases to the new day's opening equity.
        var nextDay = kernel.Decide(breached, new DayRolled(Day2)).State;
        nextDay.Protection.InProtectionMode.Should().BeFalse("the daily reset clears daily-DD protection (C4)");
        nextDay.Drawdown.CurrentDailyDrawdown.Should().Be(0m, "day 2 opens flat (K-GAP-1 re-base)");
        PreTradeGate.Evaluate(nextDay, GFx.Proposal(), Rules, GFx.Profile, GFx.Sizing, GFx.SymInfo, [])
            .Accepted.Should().BeTrue("trading resumes the next day");
    }

    [Fact] // F2 — an overall/max loss is terminal: it does NOT clear on the daily reset (unlike daily)
    public void Ftmo_MaxLossLimit_IsTerminal_UnlikeDailyLimit()
    {
        var kernel = Kernel();

        // Daily-DD protection: clears on the next day (the F1 case) — trading would resume.
        var daily = GFx.State() with
        {
            Protection = new ProtectionState(true, ProtectionCause.DailyDrawdown, "daily", "NextTradingDay", null),
        };
        kernel.Decide(daily, new DayRolled(Day2)).State.Protection.InProtectionMode
            .Should().BeFalse("daily-DD protection is per-day");

        // Overall/max breach with a terminal reset policy (FTMO blows the account): the day roll must NOT
        // clear it, and the gate stays closed.
        var overall = GFx.State() with
        {
            Protection = new ProtectionState(true, ProtectionCause.MaxDrawdown, "max", "AccountReset", null),
        };
        var afterRoll = kernel.Decide(overall, new DayRolled(Day2)).State;
        afterRoll.Protection.InProtectionMode.Should().BeTrue("an overall max-loss breach is terminal");
        GateReject(afterRoll).Should().Be("PROTECTION_MODE_ACTIVE", "the run stays halted after a max-loss breach");
    }

    [Fact(Skip = "F4: minimum-trading-days / consistency rule is not modelled yet — breadcrumb per TEST-PLAN F4")]
    public void Ftmo_MinTradingDays_Tracked() { }
}
