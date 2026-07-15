using System.Text.Json;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// Golden Replay Oracle — the gate that locks current engine behavior before ANY kernel cutover.
///
/// Runs a fixed, deterministic bar fixture through the CURRENT engine (EngineHarnessBuilder
/// → TradingLoop → RiskManager → AccountProcessor → OrderDispatcher) and snapshots the full
/// output: ordered trades, equity/drawdown, and every decision-journal line.
///
/// The snapshot is committed as golden-snapshot.json. Every subsequent A2 cutover step (wiring
/// the kernel) MUST keep this test green — the snapshot should only change when we deliberately
/// fix a bug and review the diff.
///
/// First run: writes the baseline (test passes). Subsequent runs: asserts against the baseline.
/// </summary>
[Trait("Category", "GoldenReplay")]
[Trait("Speed", "Fast")]
public sealed class GoldenReplayTests
{
    private static readonly string SnapshotDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "GoldenReplay");

    private static readonly string SnapshotPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "GoldenReplay", "golden-snapshot.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    static GoldenReplayTests()
    {
        // Ensure the directory exists for the baseline write.
        Directory.CreateDirectory(SnapshotDir);
    }

    [Fact]
    public async Task GoldenReplay_MatchesBaseline()
    {
        // --- Arrange ---
        var bars = GoldenBarFixture.Create();
        var strategy = new AlwaysSignalStrategy();

        await using var harness = await new EngineHarnessBuilder()
            .WithBars(bars)
            .WithStrategy(strategy)
            .WithInitialBalance(10_000m)
            .WithRuleSet("ftmo-standard")
            .WithFlattenAtFraction(0.9m)
            .BuildAsync();

        // --- Act ---
        await harness.DriveBarsAsync(bars);

        // --- Capture ---
        var snapshot = Capture(harness, bars.Count);

        // --- Serialize ---
        var actualJson = OracleNormalizer.Serialize(snapshot);

        // --- Assert ---
        if (!File.Exists(SnapshotPath))
        {
            // First run: write the baseline.
            File.WriteAllText(SnapshotPath, actualJson);
            // Still do a smoke-check that we produced meaningful output.
            snapshot.BarCount.Should().BeGreaterThan(0, "the engine must process bars");
            snapshot.Trades.Should().NotBeEmpty("a down-leg fixture with AlwaysSignal must produce at least one trade");
            return;
        }

        var expectedJson = File.ReadAllText(SnapshotPath);
        actualJson.Should().Be(expectedJson, "the golden snapshot must be bit-identical to the committed baseline");
    }

    private static GoldenSnapshot Capture(EngineHarness harness, int barCount)
    {
        var trades = harness.ClosedTrades
            .Select(t => new GoldenTrade(
                t.Direction.ToString(),
                t.Lots,
                t.EntryPrice,
                t.ExitPrice,
                t.ExitReason))
            .ToList();

        var journal = harness.DecisionJournal.Records
            .Select(j => new GoldenJournalEntry(
                j.PhaseBefore ?? j.Event,
                j.Event,
                j.GuardResult,
                j.Reason))
            .ToList();

        var dd = harness.Risk.Drawdown;
        var risk = new GoldenRiskState(
            dd.PeakEquity,
            dd.CurrentDailyDrawdown,
            dd.CurrentMaxDrawdown,
            harness.Risk.CurrentState.InProtectionMode,
            harness.Risk.CurrentState.ProtectionReason);

        return new GoldenSnapshot(barCount, trades, journal, risk);
    }
}
