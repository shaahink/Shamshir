using TradingEngine.Engine;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// AF0 equivalence oracle (PLAN-FINISH-AB). Proves the pure kernel gate (<see cref="PreTradeGate"/> +
/// <see cref="KernelSizing"/>) reproduces the <b>old engine's</b> order-decision sequence captured in
/// <c>golden-snapshot.json</c> — not just the first order's magic number (the weakness in
/// KernelAcceptanceTests), but the full sizing trajectory the golden encodes:
///
///   • Order 1 @ equity 10,000, no drawdown        → ACCEPT, 0.20 lots, risk 100.00
///   • Order 2 @ equity  9,900 after trade 1's SL   → ACCEPT, 0.19 lots, risk  95.00
///
/// This is the gate-equivalence guarantee the production cutover (replacing OrderDispatcher in
/// TradingLoop with the kernel) depends on. Drawdown is advanced with the SAME <see cref="DrawdownReducer"/>
/// the kernel uses, so the equity→sizing path matches the golden run's economics exactly.
/// </summary>
[Trait("Category", "GoldenReplay")]
[Trait("Speed", "Fast")]
public sealed class KernelGoldenEquivalenceTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");

    // Mirrors the golden harness SymbolInfo: contract 100k, pip 0.0001 ⇒ pipValuePerLot = 10.
    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private const decimal PipValuePerLot = 10m; // ContractSize * PipSize
    private const decimal SlPips = 50m;          // golden: entry 1.0970 → SL 1.0920

    private static (KernelConfig Config, EngineState State) Build(decimal balance, decimal equity, DrawdownState dd)
    {
        // Same profile + ruleset the golden harness uses (EngineHarnessBuilder + GoldenReplayTests).
        var profile = new RiskProfile(
            "standard", "Standard", 0.01, 0.05, 0.10, 100.0, 0.10, 0.5, 0.1, 5,
            false, "ftmo-standard", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);
        var ruleSet = new PropFirmRuleSet(
            "ftmo-standard", "ftmo-standard", "Fixed", 0.05, 0.10, 0.10, 0,
            "BalancePlusFloating", "22:00:00", "UTC",
            false, "High", 0, 0,
            false, "21:00:00", "20:00:00", "NextTradingDay", false);
        var constraints = ConstraintSet.Resolve(profile, ruleSet);
        var sizing = new SizingPolicyOptions { FlattenAtFraction = 0.9 };

        var config = new KernelConfig(
            constraints, profile, sizing,
            ResolveSymbol: _ => EurusdInfo,
            ProjectOpenPositions: _ => [],
            Seed: 42);

        var state = new EngineState(
            new Dictionary<Guid, PositionState>(),
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            dd, 0, ProtectionState.None, new AccountView(balance, equity, equity - balance));

        return (config, state);
    }

    private static OrderProposed Proposal() => new(
        Guid.NewGuid(), Eurusd, TradeDirection.Long, OrderType.Market,
        null, new Price(1.0920m), null, "always", 1.0970m,
        SlPips, PipValuePerLot, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Gate_ReproducesGoldenSizingSequence_UnderDrawdown()
    {
        // ── Order 1: fresh account, no drawdown ──
        var dd0 = DrawdownReducer.CreateInitial(10_000m, "Fixed");
        var (config1, state1) = Build(10_000m, 10_000m, dd0);

        var gate1 = PreTradeGate.Evaluate(
            state1, Proposal(), config1.Constraints, config1.Profile, config1.Sizing,
            EurusdInfo, config1.ProjectOpenPositions(state1));

        gate1.Accepted.Should().BeTrue("the golden run's first order is accepted");
        gate1.Lots.Should().Be(0.20m, "golden trade 1 = 0.20 lots");
        gate1.RiskAmount.Should().Be(100.00m, "golden trade 1 risk = 50 pips × $10 × 0.20 = 100.00");

        // ── Trade 1 hits its 50-pip SL: −100 on 0.20 lots ⇒ equity 10,000 → 9,900 (golden DD = 1%) ──
        var dd1 = DrawdownReducer.Apply(dd0, 9_900m);
        dd1.CurrentMaxDrawdown.Should().Be(0.01m, "a 100 loss on 10,000 is a 1% drawdown (golden finalRisk)");

        // ── Order 2: reduced equity + 1% drawdown ──
        var (config2, state2) = Build(9_900m, 9_900m, dd1);
        var gate2 = PreTradeGate.Evaluate(
            state2, Proposal(), config2.Constraints, config2.Profile, config2.Sizing,
            EurusdInfo, config2.ProjectOpenPositions(state2));

        gate2.Accepted.Should().BeTrue("the golden run's second order is accepted");
        gate2.Lots.Should().Be(0.19m, "golden order 2 = 0.19 lots (9,900 × 1% ÷ 500 = 0.198 → 0.01 step)");
        gate2.RiskAmount.Should().Be(95.00m, "golden order 2 risk = 50 pips × $10 × 0.19 = 95.00");
    }
}
