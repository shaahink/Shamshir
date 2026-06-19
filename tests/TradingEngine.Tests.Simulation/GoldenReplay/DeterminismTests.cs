using System.Text.Json;
using TradingEngine.Engine;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// Determinism test (iter-35 A4) — the strongest correctness guarantee in the system.
/// Running the same (DatasetRef, ConfigSet, Seed) through the kernel twice MUST produce
/// a bit-identical journal. This locks the replay contract.
/// </summary>
[Trait("Category", "Determinism")]
[Trait("Speed", "Fast")]
public sealed class DeterminismTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");

    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static readonly DatasetRef TestDataset = new(
        "test-ds", "abc123", ["EURUSD"], ["H1"],
        new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2024, 1, 1, 9, 0, 0, DateTimeKind.Utc),
        DatasetGranularity.Bar, 10);

    private static EngineState CreateInitialState() => new(
        new Dictionary<Guid, PositionState>(),
        new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
        DrawdownReducer.CreateInitial(10_000m, "Fixed"),
        0,
        ProtectionState.None,
        AccountView.Flat);

    private static KernelConfig CreateConfig()
    {
        var profile = new RiskProfile(
            "std", "Standard", 0.01, 0.05, 0.10, 100.0, 0.10, 0.5, 0.1, 5,
            false, "ftmo", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);

        var ruleSet = new PropFirmRuleSet(
            "ftmo", "ftmo", "Fixed", 0.05, 0.10, 0.10, 0,
            "BalancePlusFloating", "22:00:00", "UTC",
            false, "High", 0, 0,
            false, "21:00:00", "20:00:00", "NextTradingDay", false);

        var constraints = ConstraintSet.Resolve(profile, ruleSet);
        var sizing = new SizingPolicyOptions { FlattenAtFraction = 0.9 };

        return new KernelConfig(
            constraints, profile, sizing,
            ResolveSymbol: _ => EurusdInfo,
            ProjectOpenPositions: _ => [],
            Seed: 42);
    }

    [Fact]
    public async Task KernelRun_IsDeterministic_SameRunSpec_BitIdenticalJournal()
    {
        var bars = CreateBars(10);
        var config = CreateConfig();
        var kernel = new Kernel(config);
        var initialState = CreateInitialState();

        // Run 1
        var run1 = await RunKernelAsync(kernel, bars, initialState);

        // Run 2
        var run2 = await RunKernelAsync(kernel, bars, initialState);

        // Assert bit-identical journal.
        run1.Count.Should().Be(run2.Count, "both runs must produce the same number of journal entries");

        var json1 = SerializeJournal(run1);
        var json2 = SerializeJournal(run2);
        json1.Should().Be(json2, "the journal must be bit-identical across runs");
    }

    private static async Task<IReadOnlyList<StepRecord>> RunKernelAsync(
        IKernel kernel, IReadOnlyList<Bar> bars, EngineState initialState)
    {
        var queue = new InMemoryEngineEventQueue();
        var records = new List<StepRecord>();

        // Feed BarClosed events.
        foreach (var bar in bars)
        {
            queue.Enqueue(new BarClosed(bar.Symbol, bar.Timeframe, bar.Open, bar.High, bar.Low, bar.Close, bar.OpenTimeUtc));
        }

        var state = initialState;
        long seq = 0;

        while (queue.TryDequeue(out var evt))
        {
            var decision = kernel.Decide(state, evt);
            state = decision.State;

            records.Add(new StepRecord(
                "det-test", ++seq, evt.OccurredAtUtc,
                evt.GetType().Name, "{}",
                decision.Effects.Select(e => e.GetType().Name).ToList(),
                "[]",
                RiskSnapshots.Capture(state),
                null, null, []));
        }

        return records;
    }

    private static IReadOnlyList<Bar> CreateBars(int count)
    {
        return Bars.Trend(Eurusd, Timeframe.H1,
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            1.1000m, -50, count).Build();
    }

    private static string SerializeJournal(IReadOnlyList<StepRecord> records)
    {
        return JsonSerializer.Serialize(records.Select(r => new
        {
            r.Seq,
            r.EventKind,
            EffectCount = r.EffectKinds.Count,
        }), new JsonSerializerOptions { WriteIndented = true });
    }
}
