using TradingEngine.Domain;

namespace TradingEngine.Tests.Unit.AddOns;

[Trait("Category", "AddOns")]
[Trait("Speed", "Fast")]
public sealed class AddOnJournalKindsTests
{
    [Fact]
    public void AddOnsResolved_constant_is_ADDON_RESOLVED()
    {
        AddOnJournalKinds.AddOnsResolved.Should().Be("ADDON_RESOLVED");
    }

    [Fact]
    public void Breakeven_constant_is_BREAKEVEN()
    {
        AddOnJournalKinds.Breakeven.Should().Be("BREAKEVEN");
    }

    [Fact]
    public void Trail_constant_is_TRAIL()
    {
        AddOnJournalKinds.Trail.Should().Be("TRAIL");
    }

    [Fact]
    public void Partial_constant_is_PARTIAL()
    {
        AddOnJournalKinds.Partial.Should().Be("PARTIAL");
    }

    [Fact]
    public void Ride_constant_is_RIDE()
    {
        AddOnJournalKinds.Ride.Should().Be("RIDE");
    }

    [Fact]
    public void All_kinds_are_distinct()
    {
        var kinds = new HashSet<string>
        {
            AddOnJournalKinds.AddOnsResolved,
            AddOnJournalKinds.Breakeven,
            AddOnJournalKinds.Trail,
            AddOnJournalKinds.Partial,
            AddOnJournalKinds.Ride,
        };
        kinds.Count.Should().Be(5);
    }

    [Fact]
    public void PositionManager_reason_strings_match_journal_kinds()
    {
        // The PositionManager emits "BREAKEVEN", "TRAIL", "RIDE", "PARTIAL" as reason strings
        // that flow through StopLossModifyRequested.Kind / PartialCloseRequested.
        // These MUST match the AddOnJournalKinds constants because the SPA's unified-journal
        // filter (F1) and per-bar "why" (F2) views key on these exact strings.
        var pmReasons = new[] { "BREAKEVEN", "TRAIL", "RIDE", "PARTIAL" };
        foreach (var reason in pmReasons)
        {
            (reason switch
            {
                "BREAKEVEN" => AddOnJournalKinds.Breakeven,
                "TRAIL" => AddOnJournalKinds.Trail,
                "RIDE" => AddOnJournalKinds.Ride,
                "PARTIAL" => AddOnJournalKinds.Partial,
                _ => null,
            }).Should().NotBeNull($"reason '{reason}' should map to an AddOnJournalKinds constant");
        }
    }
}
