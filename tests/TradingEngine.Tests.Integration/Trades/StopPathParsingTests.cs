using TradingEngine.Web.Api;

namespace TradingEngine.Tests.Integration.Trades;

// X3 — the trade chart's stop path is parsed out of journaled BREAKEVEN/TRAIL StepRecords. The
// EventJson is the kernel's StopLossModifyRequested serialized with EventJsonOpts (PascalCase, no
// naming policy), so NewStopLoss is a Price value object: {"Value":1.2345}. These pin that contract.
[Trait("Category", "Infrastructure")]
public sealed class StopPathParsingTests
{
    private static readonly Guid Pos = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void ParsesPascalCaseEvent_WithPriceValueObject()
    {
        var json = $$"""{"PositionId":"{{Pos}}","NewStopLoss":{"Value":1.08425},"OccurredAtUtc":"2026-03-04T10:00:00Z","Kind":"TRAIL"}""";
        TradesController.ParseStopMove(json, Pos).Should().Be(1.08425m);
    }

    [Fact]
    public void FiltersOutOtherPositions()
    {
        var json = $$"""{"PositionId":"{{Guid.NewGuid()}}","NewStopLoss":{"Value":1.08425},"Kind":"TRAIL"}""";
        TradesController.ParseStopMove(json, Pos).Should().BeNull();
    }

    [Fact]
    public void ToleratesBareNumberStopLoss()
    {
        var json = $$"""{"PositionId":"{{Pos}}","NewStopLoss":2345.5}""";
        TradesController.ParseStopMove(json, Pos).Should().Be(2345.5m);
    }

    [Fact]
    public void MalformedOrEmptyJson_ReturnsNull()
    {
        TradesController.ParseStopMove("", Pos).Should().BeNull();
        TradesController.ParseStopMove("not json", Pos).Should().BeNull();
        TradesController.ParseStopMove("{}", Pos).Should().BeNull();
    }
}
