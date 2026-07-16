using FluentAssertions;
using TradingEngine.Domain;
using TradingEngine.Engine;

namespace TradingEngine.Tests.Unit.Kernel;

// F26 (L1, iter-viability): the gate's worst-case candidate loss must dispatch on CommissionType.
// A UsdPerMillionUsdVolume rate of 45 misread as $45/lot/side overstated round-trip commission ~9x
// on FX, rejecting trades near the daily floor that the venue would charge cents for.
public sealed class PreTradeGateCommissionTests
{
    private static SymbolInfo EurUsd(decimal commissionRate, CommissionType type) => new(
        Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100000m, 0.01m, 100m, 0.01m,
        0.0333m, 0.0001m, "USD",
        CommissionPerLotPerSide: commissionRate, CommissionType: type);

    private static readonly ConstraintSet Constraints = new(
        "test", 0.05m, 0.10m, 0.05m, 0.10m, 0.10m,
        "Trailing", DailyDdBase.InitialBalance,
        0.01m, 5, 0.20m, false, false, false,
        DailyDdEnabled: true, MaxDdEnabled: false,
        BudgetEnabled: false, MaxPositionsEnabled: false,
        GovernorEnabled: false);

    // RiskPct is a FRACTION (0.01 = 1% of equity): equity 9,620 -> risk $96.20 -> 0.48 lots at a
    // 20-pip SL and $10/pip/lot.
    private static readonly RiskProfile Sizing = new(
        "test", "Test", 0.01, 5.0, 10.0, 100.0, 10.0, 0.5, 0.1, 5,
        false, "ftmo", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);

    private static OrderProposed Proposal() => new(
        Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
        new Price(1.1000m), new Price(1.1050m), "test-strat",
        1.1000m, 20m, 10m, // 20-pip SL, $10/pip/lot
        new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc));

    // Initial balance 10,000 -> daily floor 9,500. Current equity 9,620 leaves $120 headroom.
    // At 1% risk sizing: 0.48 lots, SL loss $96. True per-million commission on ~$52.8k notional
    // is ~$4.75 round-trip (fits: 96 + 4.75 < 120); the per-lot misread charged 0.48 x 45 x 2
    // = $43.20 (breaches: 96 + 43.20 > 120).
    private static EngineState NearDailyFloorState() => new(
        new Dictionary<Guid, PositionState>(),
        new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
        DrawdownReducer.CreateInitial(10_000m, "Fixed"),
        0, ProtectionState.None, new AccountView(9_620m, 9_620m, 0m));

    [Fact]
    public void PerMillionCommission_UsesNotionalMath_NotPerLotDollars()
    {
        var symbol = EurUsd(45m, CommissionType.UsdPerMillionUsdVolume);

        var result = PreTradeGate.Evaluate(NearDailyFloorState(), Proposal(), Constraints, Sizing,
            new SizingPolicyOptions(), symbol, Array.Empty<ProjectedPosition>());

        result.Accepted.Should().BeTrue(
            $"a $45/M rate on ~$52.8k notional is cents, not $45/lot. Reason: {result.RejectReason}, " +
            $"sizing: {result.Sizing}");
    }

    [Fact]
    public void AbsolutePerLotCommission_StillChargedPerLot_AndBreaches()
    {
        // Same scenario, but the venue genuinely charges $45 per lot per side — the gate must
        // still project 0.48 x 45 x 2 = $43.20 and reject. Proves the dispatch differentiates.
        var symbol = EurUsd(45m, CommissionType.AbsolutePerLot);

        var result = PreTradeGate.Evaluate(NearDailyFloorState(), Proposal(), Constraints, Sizing,
            new SizingPolicyOptions(), symbol, Array.Empty<ProjectedPosition>());

        result.Accepted.Should().BeFalse();
        result.RejectReason.Should().Be("WorstCaseDDWouldBreachDaily");
    }
}
