using TradingEngine.Engine;
using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// End-to-end proof of the iter-36 K-GAP-1 fix: the kernel backtest loop now emits the prop-firm
/// day/week/month roll when a bar's sim-time crosses the reset boundary, so a multi-day run actually
/// resets. Before the fix the production loop never enqueued a roll (the reducer handlers were dead),
/// silently reintroducing C4 (protection never auto-exits) + H7 (governor never daily-resets) across days.
///
/// AGENT VERIFY: this is the wiring-level test (the boundary math + the reducer re-base are pinned by the
/// pure <c>ResetClockTests</c> / <c>ResetReducerTests</c>). Build + run; if the trend fixture or harness
/// shifts, adjust the fixture length — the assertion (one DayRolled per crossed 22:00 boundary) is the spec.
/// </summary>
[Trait("Category", "KernelAcceptance")]
[Trait("Speed", "Fast")]
public sealed class KernelResetMultiDayTests
{
    [Fact]
    public async Task KernelLoop_MultiDay_EmitsDayRolledAtEachResetBoundary()
    {
        // 60 H1 bars from 2024-01-01 00:00 UTC ≈ 2.5 days. The FTMO ruleset resets at 22:00 UTC, crossed on
        // day 1 (hour 22) and day 2 (hour 46) → exactly two DayRolled events through the kernel loop.
        var bars = Bars.Trend(
            GoldenBarFixture.Symbol, GoldenBarFixture.Timeframe,
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1.1000m, -100, 60).Build();

        var run = await KernelLoopHarness.RunGoldenAsync(
            bars: bars, resetConfig: ResetConfig.FromRuleSet("22:00:00", "UTC"));

        run.Records.Count(r => r.EventKind == nameof(DayRolled))
            .Should().Be(2, "the 60-hour run crosses the 22:00 UTC daily reset on two distinct days");
    }

    [Fact]
    public async Task KernelLoop_SingleDayGolden_EmitsNoRoll_EvenWithResetClock()
    {
        // The 20-bar golden fixture spans 00:00–19:00 of one UTC day and never reaches 22:00 — so even with
        // a reset clock configured, no roll fires. This guards that the new step doesn't over-fire and keeps
        // the single-day golden behaviour-identical.
        var run = await KernelLoopHarness.RunGoldenAsync(
            resetConfig: ResetConfig.FromRuleSet("22:00:00", "UTC"));

        run.Records.Any(r => r.EventKind is nameof(DayRolled) or nameof(WeekRolled) or nameof(MonthRolled))
            .Should().BeFalse("a single intraday session crosses no reset boundary");
    }
}
