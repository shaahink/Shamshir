using System.Text.Json;
using TradingEngine.Engine;
using TradingEngine.Host;
using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// iter-37 Phase J — proves the StepRecord journal is the single source of truth the iter-36 K5 cutover
/// promised, so the iter-37 frontend (F1 join, F2 funnel, F4 report) builds on it without fabricating.
/// Runs over the deterministic golden harness (<see cref="KernelLoopHarness"/>).
/// </summary>
[Trait("Category", "Journal")]
[Trait("Speed", "Fast")]
public sealed class JournalSourceOfTruthTests
{
    private static Guid OrderIdOf(StepRecord r) =>
        JsonDocument.Parse(r.EventJson).RootElement.GetProperty("OrderId").GetGuid();

    [Fact] // J1 — one StepRecord per kernel event; Seq gap-free, strictly increasing from 1
    public async Task Journal_EveryKernelEvent_ProducesExactlyOneStepRecord()
    {
        var run = await KernelLoopHarness.RunGoldenAsync();

        run.Records.Should().NotBeEmpty();
        run.Records.Select(r => r.Seq)
            .Should().Equal(Enumerable.Range(1, run.Records.Count).Select(i => (long)i),
                "Seq is a gap-free, strictly-increasing 1..N stream");
    }

    [Fact] // J1 — the F1 join key: every fill joins back to its proposed order by OrderId
    public async Task Journal_OrderAndFill_ShareOrderId()
    {
        var run = await KernelLoopHarness.RunGoldenAsync();

        var proposed = run.Records.Where(r => r.EventKind == nameof(OrderProposed)).Select(OrderIdOf).ToHashSet();
        var filled = run.Records.Where(r => r.EventKind == nameof(OrderFilled)).Select(OrderIdOf).ToList();

        proposed.Should().NotBeEmpty();
        filled.Should().NotBeEmpty();
        filled.Should().OnlyContain(id => proposed.Contains(id), "an OrderFilled shares the OrderProposed's OrderId");
    }

    [Fact] // J1 — a CLOSE exposes itemized costs (present even at zero on the FakeVenue) + the H29 identity
    public async Task Journal_Close_ExposesCosts()
    {
        var run = await KernelLoopHarness.RunGoldenAsync();

        run.ClosedTrades.Should().NotBeEmpty("the golden fixture closes a trade");
        var r = run.ClosedTrades[0].Result;
        r.NetPnL.Amount.Should().Be(r.GrossPnL.Amount - r.Commission.Amount - r.Swap.Amount,
            "Net == Gross - Commission - Swap (the F4/H29 reconciliation identity; costs present, not absent)");
        run.Records.Should().Contain(rec => rec.EffectKinds.Contains(nameof(PublishTradeClosed)),
            "the close lands on the single journal as a PublishTradeClosed effect");
    }

    [Fact] // J2 — per-strategy verdicts ride on each evaluated BarClosed (the F2 funnel data)
    public async Task Journal_BarClosed_CarriesPerStrategyVerdicts()
    {
        var run = await KernelLoopHarness.RunGoldenAsync();

        var withVerdicts = run.Records
            .Where(r => r.EventKind == nameof(BarClosed) && r.StrategyVerdicts.Count > 0).ToList();

        withVerdicts.Should().NotBeEmpty("evaluated bars carry their per-strategy verdicts");
        withVerdicts.Should().OnlyContain(
            r => r.StrategyVerdicts.All(v => !string.IsNullOrEmpty(v.StrategyId) && v.Reason != null),
            "each verdict names its strategy + a readable reason (no null/[object Object])");
    }

    [Fact] // J2 — funnel per-bar rows total the run's bar count (computed off StepRecords, not BarEvaluations)
    public async Task Journal_Funnel_TotalsMatchBarCount()
    {
        var run = await KernelLoopHarness.RunGoldenAsync();

        run.Records.Count(r => r.EventKind == nameof(BarClosed))
            .Should().Be(GoldenBarFixture.Create().Count, "one BarClosed step per bar");
    }

    [Fact] // J3 — determinism extended to the multi-day (day-roll) scope
    public async Task Journal_Determinism_ByteIdenticalAcrossRuns_MultiDay()
    {
        var bars = Bars.Trend(GoldenBarFixture.Symbol, GoldenBarFixture.Timeframe,
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1.1000m, -100, 60).Build();
        var cfg = ResetConfig.FromRuleSet("22:00:00", "UTC");

        var run1 = await KernelLoopHarness.RunGoldenAsync(bars: bars, resetConfig: cfg);
        var run2 = await KernelLoopHarness.RunGoldenAsync(bars: bars, resetConfig: cfg);

        run1.JournalJson.Should().Be(run2.JournalJson,
            "a multi-day replay reproduces the journal byte-identically, including the day-roll events");
    }
}
