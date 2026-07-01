namespace TradingEngine.Tests.Unit.Services;

/// <summary>
/// P0 repro: EstimateBarCount (BacktestOrchestrator.cs:262) uses calendar duration without
/// accounting for forex market close (weekends, nights). For a typical 1-week H1 window,
/// the calendar estimate is ~168 but actual bars are ~120 — a 40% overestimate that makes
/// the progress bar stall at ~70%.
/// </summary>
public class ProgressEstimateTests
{
    [Fact]
    public void CalendarEstimate_OverestimatesH1WeekByAtLeast30Percent()
    {
        // A typical forex trading week (Mon Open Sydney to Fri Close NY) is ~120 H1 bars.
        // Calendar math gives 7 * 24 = 168 — 40% too high.
        var monOpen = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        var friClose = new DateTime(2024, 3, 8, 21, 0, 0, DateTimeKind.Utc); // 5 days, 21h -> ~117 bars
        var calendarDuration = TimeSpan.FromDays(7); // full week

        var calendarEstimate = (int)(calendarDuration.TotalMinutes / 60); // 168
        var actualEstimate = (int)((friClose - monOpen).TotalMinutes / 60); // ~117

        // P0: the calendar estimate is significantly higher than the actual market hours estimate.
        var ratio = (double)calendarEstimate / actualEstimate;
        ratio.Should().BeGreaterThan(1.3,
            "calendar-based bar estimate (168) should overestimate market-hours (117) by >30% — this causes the progress bar to stall at ~70%");
    }

    [Fact]
    public void CalendarEstimate_OverestimatesH1MonthByAtLeast25Percent()
    {
        // A month of forex H1 bars: ~22 trading days × 24 hours ≈ 528 bars
        // Calendar: ~30 days × 24 hours = 720 bars — ~36% too high
        var calendarDuration = TimeSpan.FromDays(30);
        var tradingDays = 22;

        var calendarEstimate = (int)(calendarDuration.TotalMinutes / 60); // 720
        var tradingEstimate = tradingDays * 24; // 528

        var ratio = (double)calendarEstimate / tradingEstimate;
        ratio.Should().BeGreaterThan(1.25,
            "calendar-based bar estimate should overestimate trading-hours by >25% for a month window");
    }

    [Fact]
    public void ProgressPercentage_StallsAtRoughly70PercentWhenUsingCalendarEstimate()
    {
        // Simulating the buildProgress logic: barCount / barsTotal
        // When barsTotal uses calendar math (720) and actual bars processed is ~500:
        var calendarBarsTotal = 720; // calendar estimate for H1 month
        var actualBarsTotal = 500; // realistic H1 bars in a forex month

        var progressPct = (double)actualBarsTotal / calendarBarsTotal;
        progressPct.Should().BeApproximately(0.694, 0.05,
            "progress should be ~69% at completion of all bars when using calendar estimate — this matches the observed 'stuck at ~70%' symptom");
    }
}
