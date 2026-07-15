using TradingEngine.Engine;
using KernelCore = TradingEngine.Engine.Kernel;

namespace TradingEngine.Tests.Unit.Kernel;

/// <summary>
/// iter-37 Phase J (J1) — a rejected proposal surfaces a readable, NAMED violation on the journal's
/// decision record (never null or a stringified object). This is the data the F1 journal view renders.
/// </summary>
[Trait("Category", "Kernel")]
[Trait("Speed", "Fast")]
public sealed class JournalRejectTests
{
    [Fact]
    public void Journal_Reject_ExposesNamedViolation()
    {
        var kernel = new KernelCore(new KernelConfig(
            GFx.Constraints(), GFx.Profile, GFx.Sizing, _ => GFx.SymInfo, _ => [], Seed: 42));

        // SL far wider than the profile's MaxSlPips (100) → a named SL_TOO_WIDE rejection.
        var decision = kernel.Decide(GFx.State(), GFx.Proposal(slPips: 500m));

        var record = decision.Effects.OfType<RecordDecisionEvent>().Single();
        record.Decision.Reason.Should().NotBeNullOrWhiteSpace();
        record.Decision.Reason.Should().StartWith("SL_TOO_WIDE", "the violation is a readable name");
        decision.Effects.OfType<SubmitOrder>().Should().BeEmpty("a rejected proposal submits no order");
    }
}
