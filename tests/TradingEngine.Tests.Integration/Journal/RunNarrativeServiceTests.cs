using System.Text.Json;
using System.Text.Json.Serialization;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Web.Services;

namespace TradingEngine.Tests.Integration.Journal;

/// <summary>
/// M3.1/M3.2 regression guard. The live monitor journal feeds off
/// <c>GET /api/runs/{id}/narrative</c> → <see cref="RunNarrativeService.BuildNarrative"/>. That projection
/// MUST read the same <c>EventJson</c> shape the sink writes: PascalCase property names, value objects
/// (Symbol/Price) nested as <c>{"Value":..}</c>, enums as member names ("Long"), and add-on events keyed by
/// their canonical <see cref="AddOnJournalKinds"/> EventKind. An earlier draft read invented camelCase fields
/// (accepted/entryPrice/lots) against this schema, so every headline came out empty or "rejected". These
/// tests serialize REAL events with the production options and assert real headlines.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class RunNarrativeServiceTests
{
    // Mirrors SqliteStepRecordSink.EventJsonOpts exactly: no naming policy (PascalCase) + string enums.
    private static readonly JsonSerializerOptions EventJsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly DateTime T = new(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static JournalEntryEntity Entry(EngineEvent evt, string eventKind, string? decisionReason = null) => new()
    {
        RunId = "run-narr",
        Seq = 1,
        SimTimeUtc = T,
        EventKind = eventKind,
        EventJson = JsonSerializer.Serialize(evt, evt.GetType(), EventJsonOpts),
        DecisionReason = decisionReason,
    };

    private static OrderProposed Proposal() => new(
        Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market,
        LimitPrice: null, StopLoss: new Price(1.09500m), TakeProfit: new Price(1.10500m),
        StrategyId: "trend-breakout", SignalPriceMid: 1.10000m, SlPips: 50m, PipValuePerLot: 10m,
        OccurredAtUtc: T);

    [Fact]
    public void AcceptedProposal_rendersSignalWithSymbolDirectionAndStops()
    {
        var n = RunNarrativeService.BuildNarrative(Entry(Proposal(), "OrderProposed", decisionReason: "Accepted"));

        Assert.Equal("Signal", n.Category);
        Assert.Equal("action", n.Severity);
        Assert.Contains("trend-breakout", n.Headline);
        Assert.Contains("LONG", n.Headline);
        Assert.Contains("EURUSD", n.Headline);
        Assert.Contains("1.10000", n.Detail);   // signal price
        Assert.Contains("1.09500", n.Detail);   // stop-loss
        Assert.Contains("50p", n.Detail);       // SlPips
        Assert.DoesNotContain("rejected", n.Headline);
    }

    [Fact]
    public void RejectedProposal_usesGateReasonFromDecisionReason()
    {
        var n = RunNarrativeService.BuildNarrative(
            Entry(Proposal(), "OrderProposed", decisionReason: "MAX_EXPOSURE: 12.5% > cap 10%"));

        Assert.Equal("warning", n.Severity);
        Assert.Contains("rejected", n.Headline);
        Assert.Contains("EURUSD", n.Headline);
        Assert.Equal("MAX_EXPOSURE: 12.5% > cap 10%", n.Detail);
    }

    [Fact]
    public void EntryFill_rendersOpened()
    {
        var fill = new OrderFilled(Guid.NewGuid(), Symbol.Parse("GBPUSD"), 0.25m, new Price(1.27000m), T);
        var n = RunNarrativeService.BuildNarrative(Entry(fill, "OrderFilled"));

        Assert.Equal("Entry", n.Category);
        Assert.Contains("Opened", n.Headline);
        Assert.Contains("GBPUSD", n.Headline);
        Assert.Contains("1.27000", n.Detail);
        Assert.Contains("0.25 lots", n.Detail);
    }

    [Fact]
    public void CloseFill_rendersClosedWithReasonAndNet()
    {
        var fill = new OrderFilled(Guid.NewGuid(), Symbol.Parse("EURUSD"), 0.10m, new Price(1.10500m), T)
        {
            CloseReason = "TP",
            NetProfit = 123.45m,
            GrossProfit = 130m,
        };
        var n = RunNarrativeService.BuildNarrative(Entry(fill, "OrderFilled"));

        Assert.Equal("Exit", n.Category);
        Assert.Contains("Closed", n.Headline);
        Assert.Contains("EURUSD", n.Headline);
        Assert.Contains("take-profit", n.Headline);
        Assert.Contains("1.10500", n.Detail);
        Assert.Contains("net", n.Detail);
    }

    [Fact]
    public void ForceClose_isWarningSeverity()
    {
        var fill = new OrderFilled(Guid.NewGuid(), Symbol.Parse("EURUSD"), 0.10m, new Price(1.10500m), T)
        {
            CloseReason = "FORCE",
            NetProfit = -50m,
        };
        var n = RunNarrativeService.BuildNarrative(Entry(fill, "OrderFilled"));

        Assert.Equal("warning", n.Severity);
        Assert.Contains("force-closed", n.Headline);
    }

    [Fact]
    public void TrailStop_keyedByKind_rendersNewStop()
    {
        // KernelBacktestLoop.EventKindFor maps StopLossModifyRequested → its Kind ("TRAIL").
        var trail = new StopLossModifyRequested(Guid.NewGuid(), new Price(1.09800m), T, AddOnJournalKinds.Trail);
        var n = RunNarrativeService.BuildNarrative(Entry(trail, AddOnJournalKinds.Trail));

        Assert.Equal("AddOn", n.Category);
        Assert.Contains("Trail", n.Headline);
        Assert.Contains("1.09800", n.Detail);
    }

    [Fact]
    public void Breakeven_keyedByKind_rendersBreakEven()
    {
        var be = new StopLossModifyRequested(Guid.NewGuid(), new Price(1.10000m), T, AddOnJournalKinds.Breakeven);
        var n = RunNarrativeService.BuildNarrative(Entry(be, AddOnJournalKinds.Breakeven));

        Assert.Contains("break-even", n.Headline);
        Assert.Contains("1.10000", n.Detail);
    }

    [Fact]
    public void OrderRejected_showsVenueReason()
    {
        var rej = new OrderRejected(Guid.NewGuid(), Symbol.Parse("EURUSD"), "INSUFFICIENT_MARGIN", T);
        var n = RunNarrativeService.BuildNarrative(Entry(rej, "OrderRejected"));

        Assert.Equal("Risk", n.Category);
        Assert.Contains("rejected", n.Headline.ToLowerInvariant());
        Assert.Equal("INSUFFICIENT_MARGIN", n.Detail);
    }

    [Fact]
    public void OrderCancelled_showsReason()
    {
        var cancel = new OrderCancelled(Guid.NewGuid(), Symbol.Parse("EURUSD"), "ENTRY_EXPIRED", T);
        var n = RunNarrativeService.BuildNarrative(Entry(cancel, "OrderCancelled"));

        Assert.Contains("cancelled", n.Headline.ToLowerInvariant());
        Assert.Equal("ENTRY_EXPIRED", n.Detail);
    }

    [Fact]
    public void DayRolled_matchesActualEventTypeName()
    {
        var roll = new DayRolled(T);
        var n = RunNarrativeService.BuildNarrative(Entry(roll, "DayRolled"));

        Assert.Equal("System", n.Category);
        Assert.Contains("day", n.Headline.ToLowerInvariant());
    }
}
