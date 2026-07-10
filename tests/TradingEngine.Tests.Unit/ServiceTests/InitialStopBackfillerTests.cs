using TradingEngine.Services.Helpers;

namespace TradingEngine.Tests.Unit.ServiceTests;

[Trait("Category", "Services")]
public sealed class InitialStopBackfillerTests
{
    // Shape confirmed against the live DB's Journal.EventJson for EventKind='OrderProposed'
    // (PascalCase, value objects nested as {"Value": ...}).
    private const string SampleOrderProposedJson =
        """
        {"OrderId":"00000001-0000-0000-0000-000000000000","Symbol":{"Value":"EURUSD"},"Direction":"Short","OrderType":"Market","LimitPrice":null,"StopLoss":{"Value":1.16285},"TakeProfit":{"Value":1.15930},"StrategyId":"trend-breakout","EntryReason":"Break of 20-bar low","OccurredAtUtc":"2026-06-03T06:00:00"}
        """;

    private static readonly Guid SampleOrderId = Guid.Parse("00000001-0000-0000-0000-000000000000");

    [Fact]
    public void ParseOrderProposedStops_ExtractsOrderIdAndStopLoss()
    {
        var map = InitialStopBackfiller.ParseOrderProposedStops([SampleOrderProposedJson]);

        map.Should().ContainKey(SampleOrderId);
        map[SampleOrderId].Should().Be(1.16285m);
    }

    [Fact]
    public void ParseOrderProposedStops_SkipsMalformedRows_WithoutThrowing()
    {
        var map = InitialStopBackfiller.ParseOrderProposedStops(["not json", "", SampleOrderProposedJson, "{\"OrderId\":\"bad\"}"]);

        map.Should().HaveCount(1);
        map[SampleOrderId].Should().Be(1.16285m);
    }

    [Fact]
    public void Resolve_PrefersJournal_OverSnapshotFallback()
    {
        var journalStops = new Dictionary<Guid, decimal> { [SampleOrderId] = 1.16285m };
        var entrySnapshotJson = """{"reason":"x","stopLoss":1.99999}"""; // deliberately different — must be ignored

        var resolution = InitialStopBackfiller.Resolve(SampleOrderId, journalStops, entrySnapshotJson);

        resolution.Source.Should().Be(InitialStopBackfiller.Source.Journal);
        resolution.StopLoss.Should().Be(1.16285m);
    }

    [Fact]
    public void Resolve_FallsBackToSnapshot_WhenOrderIdNotInJournal()
    {
        var journalStops = new Dictionary<Guid, decimal>();
        var entrySnapshotJson = """{"reason":"x","stopLoss":1.08210}""";

        var resolution = InitialStopBackfiller.Resolve(SampleOrderId, journalStops, entrySnapshotJson);

        resolution.Source.Should().Be(InitialStopBackfiller.Source.SnapshotFallback);
        resolution.StopLoss.Should().Be(1.08210m);
    }

    [Fact]
    public void Resolve_Unresolved_WhenNeitherSourceHasIt()
    {
        var resolution = InitialStopBackfiller.Resolve(SampleOrderId, new Dictionary<Guid, decimal>(), entrySnapshotJson: null);

        resolution.Source.Should().Be(InitialStopBackfiller.Source.Unresolved);
        resolution.StopLoss.Should().BeNull();
    }

    [Fact]
    public void Resolve_Unresolved_WhenSnapshotJsonMalformed()
    {
        var resolution = InitialStopBackfiller.Resolve(SampleOrderId, new Dictionary<Guid, decimal>(), entrySnapshotJson: "not json");

        resolution.Source.Should().Be(InitialStopBackfiller.Source.Unresolved);
    }
}
