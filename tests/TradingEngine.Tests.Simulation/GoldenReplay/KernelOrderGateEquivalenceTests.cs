using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// AF2 end-to-end equivalence: drives the golden fixture through the REAL engine (TradingLoop + FakeVenue
/// + PositionTracker + bar-exit simulation) twice — once with the legacy <c>OrderDispatcher</c>, once with
/// the new <c>KernelOrderGate</c> — and asserts they produce identical trades and identical accepted-order
/// decisions. Comparing the two runs (rather than the committed snapshot) makes this robust to wall-clock
/// conditions (news/weekend) while proving the production cutover is behaviour-preserving.
/// </summary>
[Trait("Category", "GoldenReplay")]
[Trait("Speed", "Fast")]
public sealed class KernelOrderGateEquivalenceTests
{
    private static EngineHarnessBuilder Harness() => new EngineHarnessBuilder()
        .WithBars(GoldenBarFixture.Create())
        .WithInitialBalance(10_000m)
        .WithRuleSet("ftmo-standard")
        .WithFlattenAtFraction(0.9m);

    [Fact]
    public async Task KernelOrderGate_IsBehaviourEquivalent_ToOrderDispatcher()
    {
        var bars = GoldenBarFixture.Create();

        await using var legacy = await Harness().BuildAsync();
        await legacy.DriveBarsAsync(bars);

        await using var kernel = await Harness().WithKernelGate().BuildAsync();
        await kernel.DriveBarsAsync(bars);

        // Same closed trades (direction / lots / entry / exit / reason).
        kernel.ClosedTrades.Should().BeEquivalentTo(legacy.ClosedTrades,
            "the kernel order gate must close the same trades as the legacy dispatcher");
        kernel.ClosedTrades.Should().NotBeEmpty("the down-leg fixture closes at least one trade");

        // Same accepted-order decisions — this carries the full sizing trajectory (0.20 then 0.19).
        var legacyOrders = AcceptedOrders(legacy);
        var kernelOrders = AcceptedOrders(kernel);
        kernelOrders.Should().BeEquivalentTo(legacyOrders,
            "the kernel gate must accept the same orders with the same lots/risk as the legacy dispatcher");
        kernelOrders.Should().Contain(r => r!.Contains("lots=0.2000"))
            .And.Contain(r => r!.Contains("lots=0.1900"));
    }

    private static List<string?> AcceptedOrders(EngineHarness h) =>
        h.DecisionJournal.Records
            .Where(r => r.Event == "OrderSubmitted")
            .Select(r => r.Reason)
            .ToList();
}
