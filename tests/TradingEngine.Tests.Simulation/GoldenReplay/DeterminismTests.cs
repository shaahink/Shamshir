using System.Text.Json;
using TradingEngine.Engine;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

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
    public async Task KernelRun_IsDeterministic_WithPositionsOpen()
    {
        var config = CreateConfig();
        var kernel = new Kernel(config);
        var initialState = CreateInitialState();

        var run1 = await RunWithPositionsAsync(kernel, initialState);
        var run2 = await RunWithPositionsAsync(kernel, initialState);

        run1.Count.Should().Be(run2.Count, "both runs must produce same number of journal entries");

        var json1 = SerializeJournalFull(run1);
        var json2 = SerializeJournalFull(run2);
        json1.Should().Be(json2, "journal must be bit-identical across runs with positions opening/closing");
    }

    [Fact]
    public async Task KernelRun_IsDeterministic_BarClosedOnly()
    {
        var config = CreateConfig();
        var kernel = new Kernel(config);
        var initialState = CreateInitialState();

        var run1 = await RunBarsOnlyAsync(kernel, initialState);
        var run2 = await RunBarsOnlyAsync(kernel, initialState);

        run1.Count.Should().Be(run2.Count);
        var json1 = SerializeJournalFull(run1);
        var json2 = SerializeJournalFull(run2);
        json1.Should().Be(json2, "bar-only journal must be bit-identical");
    }

    private static async Task<IReadOnlyList<StepRecord>> RunWithPositionsAsync(IKernel kernel, EngineState initialState)
    {
        var queue = new InMemoryEngineEventQueue();
        var records = new List<StepRecord>();

        // Feed: OrderProposed (triggers position open) → OrderFilled → BarClosed (with SL hit) → close
        var orderId = new Guid("11111111-1111-1111-1111-111111111111");
        var now = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc);

        // OrderProposed — triggers position open
        queue.Enqueue(new OrderProposed(
            orderId, Eurusd, TradeDirection.Long, OrderType.Market,
            null, new Price(1.0950m), new Price(1.1050m), "test",
            1.1000m, 50m, 10m, now));

        // OrderFilled — moves position to Open
        queue.Enqueue(new OrderFilled(orderId, Eurusd, 0.20m, new Price(1.1000m), now));

        // Bar that hits SL
        queue.Enqueue(new BarClosed(Eurusd, Timeframe.H1, 1.1000m, 1.1010m, 1.0940m, 1.0950m, now));

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
                SerializeEffects(decision.Effects),
                RiskSnapshots.Capture(state),
                null, null, []));
        }

        return records;
    }

    private static async Task<IReadOnlyList<StepRecord>> RunBarsOnlyAsync(IKernel kernel, EngineState initialState)
    {
        var queue = new InMemoryEngineEventQueue();
        var records = new List<StepRecord>();

        var bars = CreateBars(10);
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
                SerializeEffects(decision.Effects),
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

    private static readonly JsonSerializerOptions EnumJson = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    // Serialize each effect by its RUNTIME type so subtype payloads (OrderId, prices, lots) are
    // captured. The old version serialized only effect *kind names*, so a Guid/price nondeterminism
    // in an effect would have slipped straight through — this is what makes the gate actually bite.
    private static string SerializeEffects(System.Collections.IEnumerable effects) =>
        JsonSerializer.Serialize(
            effects.Cast<object>().Select(e => JsonSerializer.Serialize(e, e.GetType(), EnumJson)).ToList());

    // Compare the FULL StepRecord across runs: seq, event kind, effect kinds, the real effect payloads
    // (ids/prices/lots), and the risk snapshot — not just {seq, kind, count}.
    private static string SerializeJournalFull(IReadOnlyList<StepRecord> records)
    {
        return JsonSerializer.Serialize(records.Select(r => new
        {
            r.Seq,
            r.EventKind,
            r.EffectKinds,
            Effects = r.EffectsJson,
            r.Risk,
        }), new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
    }
}
