using System.Text.Json;
using System.Text.Json.Serialization;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Tests.Integration.Support;
using TradingEngine.Web.Services;

namespace TradingEngine.Tests.Integration.Journal;

/// <summary>
/// M3.2 — the live monitor polls <c>GetNarrativeAsync</c> with an advancing <c>afterSeq</c> cursor. This
/// covers the query path: default exclusion of BarClosed/EquityObserved, the <c>latestSeq</c> cursor that
/// must advance past ALL fetched rows (even excluded-from-output ones) so polling can't stall, and paging.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class RunNarrativeQueryTests : IDisposable
{
    private const string RunId = "run-narr-query";
    private readonly SqliteInMemory _db = new();

    private static readonly JsonSerializerOptions EventJsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public RunNarrativeQueryTests()
    {
        using var ctx = _db.NewContext();
        var t = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        // seq 1: a proposal (surfaced). seq 2: a BarClosed (excluded by default). seq 3: a fill (surfaced).
        var proposal = new OrderProposed(
            Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market,
            null, new Price(1.095m), new Price(1.105m), "trend", 1.10m, 50m, 10m, t);
        var bar = new BarClosed(Symbol.Parse("EURUSD"), Timeframe.H1, 1.1m, 1.11m, 1.09m, 1.1m, t);
        var fill = new OrderFilled(Guid.NewGuid(), Symbol.Parse("EURUSD"), 0.1m, new Price(1.10m), t);

        ctx.JournalEntries.AddRange(
            Entry(1, "OrderProposed", proposal, "Accepted"),
            Entry(2, "BarClosed", bar),
            Entry(3, "OrderFilled", fill));
        ctx.SaveChanges();
    }

    private static JournalEntryEntity Entry(long seq, string kind, EngineEvent evt, string? decisionReason = null) => new()
    {
        RunId = RunId,
        Seq = seq,
        SimTimeUtc = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        EventKind = kind,
        EventJson = JsonSerializer.Serialize(evt, evt.GetType(), EventJsonOpts),
        DecisionReason = decisionReason,
    };

    [Fact]
    public async Task Default_excludesBars_butCursorAdvancesPastThem()
    {
        var svc = new RunNarrativeService(_db.NewContext());

        var resp = await svc.GetNarrativeAsync(RunId, afterSeq: null, kinds: null, severity: null, limit: 100);

        // BarClosed (seq 2) is filtered from OUTPUT...
        Assert.DoesNotContain(resp.Events, e => e.Headline.StartsWith("Bar closed"));
        Assert.Equal(2, resp.Events.Count);
        // ...but the cursor still advances to seq 3 so a follow-up poll won't re-fetch seq 2 forever.
        Assert.Equal(3, resp.LatestSeq);
        Assert.False(resp.HasMore);
    }

    [Fact]
    public async Task AfterSeq_pagesForward()
    {
        var svc = new RunNarrativeService(_db.NewContext());

        var resp = await svc.GetNarrativeAsync(RunId, afterSeq: 1, kinds: null, severity: null, limit: 100);

        Assert.Single(resp.Events);                 // only the fill (seq 3); bar excluded, proposal already seen
        Assert.Equal("Entry", resp.Events[0].Category);
        Assert.Equal(3, resp.LatestSeq);
    }

    [Fact]
    public async Task Kinds_filterIsApplied()
    {
        var svc = new RunNarrativeService(_db.NewContext());

        var resp = await svc.GetNarrativeAsync(RunId, afterSeq: null, kinds: new[] { "OrderFilled" }, severity: null, limit: 100);

        Assert.Single(resp.Events);
        Assert.Equal("Entry", resp.Events[0].Category);
    }

    public void Dispose() => _db.Dispose();
}
