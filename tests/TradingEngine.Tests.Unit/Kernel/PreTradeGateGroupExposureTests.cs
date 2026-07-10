using FluentAssertions;
using TradingEngine.Domain;
using TradingEngine.Engine;

namespace TradingEngine.Tests.Unit.Kernel;

public sealed class PreTradeGateGroupExposureTests
{
    private static readonly SymbolInfo EurUsd = new(
        Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100000m, 0.01m, 100m, 0.01m,
        0.0333m, 0.0001m, "USD");

    private static readonly SymbolInfo XauUsd = new(
        Symbol.Parse("XAUUSD"), SymbolCategory.Metal, "XAU", "USD",
        0.01m, 0.001m, 100, 0.01m, 10m, 0.01m, 0.05m, 0.3m);

    private static readonly ConstraintSet BaseConstraints = new(
        "test", 0.05m, 0.10m, 0.05m, 0.10m, 0.10m,
        "Trailing", DailyDdBase.InitialBalance,
        0.01m, 5, 0.20m, false, false, false,
        DailyDdEnabled: false, MaxDdEnabled: false,
        BudgetEnabled: false, MaxPositionsEnabled: false,
        GovernorEnabled: false);

    private static readonly RiskProfile BaseSizing = new(
        "test", "Test", 1.0, 5.0, 10.0, 100.0, 10.0, 0.5, 0.1, 5,
        false, "ftmo", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);

    private static OrderProposed Proposal(string symbol, decimal slPips)
    {
        var sym = Symbol.Parse(symbol);
        var info = symbol == "XAUUSD" ? XauUsd : EurUsd;
        var pipValue = info.ContractSize * info.PipSize;
        return new OrderProposed(
            Guid.NewGuid(), sym, TradeDirection.Long, OrderType.Market, null,
            new Price(1.1000m), new Price(1.1050m), "test-strat",
            1.1000m, slPips, pipValue,
            new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc));
    }

    private static EngineState FreshState(decimal equity = 10_000m) =>
        new(
            new Dictionary<Guid, PositionState>(),
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            DrawdownReducer.CreateInitial(equity, "Fixed"),
            0, ProtectionState.None, new AccountView(equity, equity, 0m));

    private static IReadOnlyList<ProjectedPosition> OpenPositions(params (string sym, decimal sl, decimal lots)[] pos) =>
        pos.Select(p => new ProjectedPosition(p.sym, p.sl, p.lots, 10m)).ToList();

    [Fact]
    public void No_groups_configured_passes_group_check()
    {
        var constraints = BaseConstraints with { ExposureGroups = null };
        var state = FreshState(10_000m);
        var proposal = Proposal("EURUSD", 20m);
        var open = OpenPositions(("EURUSD", 20m, 0.5m)); // $100 risk already open

        var result = PreTradeGate.Evaluate(state, proposal, constraints, BaseSizing,
            new SizingPolicyOptions(), EurUsd, open);

        result.Accepted.Should().BeTrue($"no groups configured should never block. Reason: {result.RejectReason}");
        result.RejectReason.Should().BeNull();
    }

    [Fact]
    public void Empty_groups_passes_group_check()
    {
        var constraints = BaseConstraints with { ExposureGroups = Array.Empty<ExposureGroup>() };
        var state = FreshState(10_000m);
        var proposal = Proposal("EURUSD", 20m);
        var open = Array.Empty<ProjectedPosition>();

        var result = PreTradeGate.Evaluate(state, proposal, constraints, BaseSizing,
            new SizingPolicyOptions(), EurUsd, open);

        result.Accepted.Should().BeTrue();
    }

    [Fact]
    public void Group_cap_rejects_when_exceeded()
    {
        var groups = new List<ExposureGroup>
        {
            new("eur-bloc", "EUR Bloc", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EURUSD", "EURGBP" }, 0.02m),
        };
        var constraints = BaseConstraints with { ExposureGroups = groups };
        var state = FreshState(10_000m);
        var proposal = Proposal("EURUSD", 20m); // at 1% risk = $100 new risk
        var open = OpenPositions(("EURUSD", 20m, 0.5m)); // $20 × 0.5 × 10 = $100 already open

        // Group total = $100 existing + $100 new = $200 / $10,000 = 2% → equals cap (2%)
        // Actually groupRisk sums slPips * lots * pipValuePerLot (from ProjectedPosition), not from sizing.
        // Let's make it exceed: existing = $200 risk, new = $100 risk → $300 / $10,000 = 3% > 2%
        var openPos = OpenPositions(("EURUSD", 20m, 1.0m)); // $20 × 1.0 × 10 = $200 risk

        var result = PreTradeGate.Evaluate(state, proposal, constraints, BaseSizing,
            new SizingPolicyOptions(), EurUsd, openPos);

        result.Accepted.Should().BeFalse();
        result.RejectReason.Should().Contain("GROUP_EXPOSURE");
        result.RejectReason.Should().Contain("eur-bloc");
    }

    [Fact]
    public void Group_cap_passes_when_under_limit()
    {
        var groups = new List<ExposureGroup>
        {
            new("eur-bloc", "EUR Bloc", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EURUSD" }, 0.05m),
        };
        var constraints = BaseConstraints with { ExposureGroups = groups };
        var state = FreshState(10_000m);
        var proposal = Proposal("EURUSD", 20m);
        var open = OpenPositions(("EURUSD", 20m, 0.1m)); // $20 risk

        var result = PreTradeGate.Evaluate(state, proposal, constraints, BaseSizing,
            new SizingPolicyOptions(), EurUsd, open);

        result.Accepted.Should().BeTrue();
    }

    [Fact]
    public void Different_symbol_does_not_affect_group_check()
    {
        var groups = new List<ExposureGroup>
        {
            new("eur-bloc", "EUR Bloc", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EURUSD" }, 0.01m),
        };
        var constraints = BaseConstraints with { ExposureGroups = groups };
        var state = FreshState(10_000m);
        var proposal = Proposal("XAUUSD", 50m); // XAUUSD is NOT in eur-bloc, slPips under 100 limit
        var open = OpenPositions(("EURUSD", 20m, 1.0m)); // EURUSD risk but XAUUSD proposal unaffected

        var result = PreTradeGate.Evaluate(state, proposal, constraints, BaseSizing,
            new SizingPolicyOptions(), XauUsd, open);

        result.Accepted.Should().BeTrue($"XAUUSD is not in eur-bloc, group check should pass. Reason: {result.RejectReason}");
    }

    [Fact]
    public void Group_exposure_is_per_group_independent()
    {
        var groups = new List<ExposureGroup>
        {
            new("eur-bloc", "EUR Bloc", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EURUSD" }, 0.01m),
            new("metals", "Metals", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "XAUUSD" }, 0.10m),
        };
        var constraints = BaseConstraints with { ExposureGroups = groups };
        var state = FreshState(10_000m);
        var proposal = Proposal("EURUSD", 20m);
        var open = OpenPositions(("EURUSD", 20m, 1.0m)); // EURUSD group full

        // This should be rejected by eur-bloc
        var result = PreTradeGate.Evaluate(state, proposal, constraints, BaseSizing,
            new SizingPolicyOptions(), EurUsd, open);
        result.Accepted.Should().BeFalse();

        // But a metals proposal should pass
        var metalsProposal = Proposal("XAUUSD", 50m); // slPips=50, under 100 limit
        var metalsResult = PreTradeGate.Evaluate(state, metalsProposal, constraints, BaseSizing,
            new SizingPolicyOptions(), XauUsd, open);
        metalsResult.Accepted.Should().BeTrue($"metals group cap is not breached. Reason: {metalsResult.RejectReason}");
    }
}
