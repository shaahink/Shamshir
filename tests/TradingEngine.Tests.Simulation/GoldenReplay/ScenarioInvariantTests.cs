using TradingEngine.Engine;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// Scenario/invariant harness (iter-35 A4). Pressure-tests the kernel by running specific bar patterns
/// and asserting risk invariants on the journal: no trade passes the gate whose worst case breaches a
/// floor, force-close fires when enabled, daily/weekly monthly enforced when configured.
/// </summary>
[Trait("Category", "Scenario")]
[Trait("Speed", "Fast")]
public sealed class ScenarioInvariantTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");

    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static EngineState CreateInitialState(decimal balance = 10_000m) => new(
        new Dictionary<Guid, PositionState>(),
        new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
        DrawdownReducer.CreateInitial(balance, "Fixed"),
        0,
        ProtectionState.None,
        new AccountView(balance, balance, 0));

    private static KernelConfig CreateConfig(decimal maxDailyLoss = 0.05m, decimal maxTotalLoss = 0.10m)
    {
        var profile = new RiskProfile(
            "std", "Standard", 0.01, (double)maxDailyLoss, (double)maxTotalLoss, 100.0,
            0.10, 0.5, 0.1, 5, false, "ftmo", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);

        var ruleSet = new PropFirmRuleSet(
            "ftmo", "ftmo", "Fixed", (double)maxDailyLoss, (double)maxTotalLoss, 0.10, 0,
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
    public void Gate_RejectsOrder_WhenWorstCaseWouldBreachDailyFloor()
    {
        var state = CreateInitialState(1_000m); // small balance = tight floor
        var config = CreateConfig(0.05m, 0.10m);

        // A trade with SL that would breach 5% daily DD on a 1000 balance (= max loss of 50).
        var proposed = new OrderProposed(
            Guid.NewGuid(), Eurusd, TradeDirection.Long, OrderType.Market,
            null, new Price(1.0900m), null, "test", 1.0950m,
            SlPips: 1000m,  // 1000 pips with $10/pip = $10,000 loss on 0.01 lots
            PipValuePerLot: 10m,
            OccurredAtUtc: DateTime.UtcNow);

        var kernel = new Kernel(config);
        var decision = kernel.Decide(state, proposed);

        // Should be rejected — either by budget or by the worst-case floor check.
        var effects = decision.Effects;
        effects.Should().Contain(e => e is RecordDecisionEvent,
            "a trade whose worst case would breach the daily floor must be rejected");
        effects.Should().NotContain(e => e is SubmitOrder,
            "no SubmitOrder effect must be emitted for a rejected proposal");
    }

    [Fact]
    public void Gate_RejectsOrder_WhenInProtectionMode()
    {
        var state = CreateInitialState(10_000m) with
        {
            Protection = ProtectionState.None.Enter(ProtectionCause.DailyDrawdown, "breach test"),
        };
        var config = CreateConfig();

        var proposed = new OrderProposed(
            Guid.NewGuid(), Eurusd, TradeDirection.Long, OrderType.Market,
            null, new Price(1.0900m), null, "test", 1.0950m,
            SlPips: 10m, PipValuePerLot: 10m, OccurredAtUtc: DateTime.UtcNow);

        var kernel = new Kernel(config);
        var decision = kernel.Decide(state, proposed);

        var rec = decision.Effects.OfType<RecordDecisionEvent>().FirstOrDefault();
        rec.Should().NotBeNull("protection mode must reject new orders");
        rec!.Decision.GuardResult.Should().Be("PROTECTION_MODE_ACTIVE");
    }

    [Fact]
    public void Gate_RejectsOrder_WhenGovernorHardStopped()
    {
        var state = CreateInitialState(10_000m) with
        {
            Governor = new GovernorState(GovernorTradingState.HardStop, 0, 0, 0, 1.0m, false, "Initial"),
        };
        var config = CreateConfig();

        var proposed = new OrderProposed(
            Guid.NewGuid(), Eurusd, TradeDirection.Long, OrderType.Market,
            null, new Price(1.0900m), null, "test", 1.0950m,
            SlPips: 10m, PipValuePerLot: 10m, OccurredAtUtc: DateTime.UtcNow);

        var kernel = new Kernel(config);
        var decision = kernel.Decide(state, proposed);

        var rec = decision.Effects.OfType<RecordDecisionEvent>().FirstOrDefault();
        rec.Should().NotBeNull("hard-stopped governor must block orders");
        rec!.Decision.GuardResult.Should().StartWith("GOVERNOR:");
    }

    [Fact]
    public void Kernel_EntersProtection_WhenEquityBreachesDailyDD()
    {
        var state = CreateInitialState(10_000m);
        var config = CreateConfig(0.05m, 0.10m);

        // Equity observed at 9,400 = 6% DD (exceeds 5% * 0.9 = 4.5% trigger).
        var equity = new EquityObserved(10_000m, 9_400m, -600m, DateTime.UtcNow);
        var kernel = new Kernel(config);
        var decision = kernel.Decide(state, equity);

        decision.State.Protection.InProtectionMode.Should().BeTrue(
            "equity at 6% DD with 5% daily limit must trigger protection");
        decision.State.Protection.Cause.Should().Be(ProtectionCause.DailyDrawdown);
    }

    [Fact]
    public void Kernel_DoesNotEnterProtection_WhenDDWithinLimits()
    {
        var state = CreateInitialState(10_000m);
        var config = CreateConfig(0.05m, 0.10m);

        // Equity at 9,700 = 3% DD (below 5% * 0.9 = 4.5% trigger).
        var equity = new EquityObserved(10_000m, 9_700m, -300m, DateTime.UtcNow);
        var kernel = new Kernel(config);
        var decision = kernel.Decide(state, equity);

        decision.State.Protection.InProtectionMode.Should().BeFalse(
            "DD within limits must not trigger protection");
    }

    [Fact]
    public void Kernel_ClearsProtection_OnDayRoll()
    {
        var state = CreateInitialState(10_000m) with
        {
            Protection = ProtectionState.None.Enter(ProtectionCause.DailyDrawdown, "test breach"),
        };
        var config = CreateConfig();

        var kernel = new Kernel(config);
        var decision = kernel.Decide(state, new DayRolled(DateTime.UtcNow));

        decision.State.Protection.InProtectionMode.Should().BeFalse(
            "daily-DD protection must clear on day roll");
    }

    [Fact]
    public void Gate_Rejects_WhenMaxPositionsReached()
    {
        var state = CreateInitialState(10_000m);
        var config = CreateConfig();

        // Fill up to MaxConcurrentPositions (5) with dummy positions.
        var positions = new Dictionary<Guid, PositionState>();
        for (int i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            var pos = PositionLifecycle.CreateIntended(
                id, Eurusd, TradeDirection.Long, 0.1m, null,
                new Price(1.0900m), null, "test");
            var (next, _) = PositionLifecycle.Apply(pos, new OrderSubmitted(
                id, Eurusd, TradeDirection.Long, 0.1m, null, "test", DateTime.UtcNow));
            (next, _) = PositionLifecycle.Apply(next, new OrderFilled(
                id, Eurusd, 0.1m, new Price(1.0950m), DateTime.UtcNow));
            positions[id] = next;
        }
        state = state with { Positions = positions };

        var proposed = new OrderProposed(
            Guid.NewGuid(), Eurusd, TradeDirection.Long, OrderType.Market,
            null, new Price(1.0850m), null, "test", 1.0900m,
            SlPips: 10m, PipValuePerLot: 10m, OccurredAtUtc: DateTime.UtcNow);

        var kernel = new Kernel(config);
        var decision = kernel.Decide(state, proposed);

        var rec = decision.Effects.OfType<RecordDecisionEvent>().FirstOrDefault();
        rec.Should().NotBeNull("max positions reached must block orders");
        rec!.Decision.GuardResult.Should().StartWith("MAX_POSITIONS");
    }
}
