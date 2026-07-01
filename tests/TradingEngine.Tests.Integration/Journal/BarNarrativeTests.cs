using System.Text.Json;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Tests.Integration.Support;
using TradingEngine.Web.Services;

namespace TradingEngine.Tests.Integration.Journal;

/// <summary>
/// iter-redesign P5 — bar narrative aggregation: prove the existing persisted journal yields per-bar
/// narratives (regime, strategy verdicts, proposals, gate rejections with numbers, risk snapshot)
/// grouped by sim-time, with no new DB table required.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class BarNarrativeTests : IDisposable
{
    private const string RunId = "run-bars-narr";
    private readonly SqliteInMemory _db = new();
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public BarNarrativeTests()
    {
        using var db = NewContext();

        var riskT1 = JsonSerializer.Serialize(
            new RiskSnapshot(10_000m, 10_000m, 0m, 0m, 0m, 0m, 0m, false, null, "Normal", 0), CamelCase);
        var riskT2 = JsonSerializer.Serialize(
            new RiskSnapshot(10_000m, 9_800m, -200m, 0.02m, 0.02m, 0.01m, 0m, false, null, "Normal", 1), CamelCase);
        var emptyRisk = JsonSerializer.Serialize(
            new RiskSnapshot(0m, 0m, 0m, 0m, 0m, 0m, 0m, false, null, "Normal", 0), CamelCase);

        var t1 = new DateTime(2024, 1, 1, 0, 1, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2024, 1, 1, 0, 2, 0, DateTimeKind.Utc);

        // Bar 1: a proposal with a numeric rejection + BarClosed with a signal verdict
        db.JournalEntries.Add(new JournalEntryEntity
        {
            RunId = RunId, Seq = 1, SimTimeUtc = t1,
            EventKind = "OrderProposed", EventJson = "{}",
            EffectKinds = "[\"SubmitOrder\",\"RecordDecisionEvent\"]",
            EffectsJson = "[]",
            RiskJson = emptyRisk,
            DecisionReason = "BudgetBlocked: openRisk=0.00 + new=100.00 = 100.00 > cap=125.00 lots=0.0100",
        });

        var verdictsJson = JsonSerializer.Serialize(
            new List<StrategyVerdict>
            {
                new("test-s1", true, true, TradeDirection.Long, "atr-breakout", null),
                new("test-s2", true, false, null, "no-signal", null),
            }, CamelCase);

        db.JournalEntries.Add(new JournalEntryEntity
        {
            RunId = RunId, Seq = 2, SimTimeUtc = t1,
            EventKind = "BarClosed", EventJson = "{}",
            EffectKinds = "[]", EffectsJson = "[]",
            RiskJson = riskT1,
            Regime = "Trending",
            VerdictsJson = verdictsJson,
        });

        // Bar 2: a fill + close + BarClosed with a position open
        db.JournalEntries.Add(new JournalEntryEntity
        {
            RunId = RunId, Seq = 3, SimTimeUtc = t2,
            EventKind = "OrderFilled", EventJson = "{}",
            EffectKinds = "[\"RegisterRisk\",\"RecordDecisionEvent\"]",
            EffectsJson = "[]",
            RiskJson = emptyRisk,
        });

        db.JournalEntries.Add(new JournalEntryEntity
        {
            RunId = RunId, Seq = 4, SimTimeUtc = t2,
            EventKind = "OrderFilled", EventJson = "{}",
            EffectKinds = "[\"PublishTradeClosed\",\"DeregisterRisk\"]",
            EffectsJson = "[]",
            RiskJson = riskT2,
            DecisionReason = "FORCE",
        });

        db.SaveChanges();
    }

    private TradingDbContext NewContext() => _db.NewContext();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetRunBars_GroupsBySimTime_AndExtractsNarrative()
    {
        await using var db = NewContext();
        var svc = new RunQueryService(
            db,
            new SqliteBacktestRunRepository(db),
            new SqliteEquityRepository(db),
            new SqliteJournalQueryRepository(db));

        var bars = await svc.GetRunBarsAsync(RunId, null, null, CancellationToken.None);

        bars.Should().HaveCount(2, "two distinct SimTimeUtc values were seeded");

        // ── Bar 1 (T1) ──
        var bar1 = bars[0];
        bar1.SimTimeUtc.Should().Be(new DateTime(2024, 1, 1, 0, 1, 0, DateTimeKind.Utc));
        bar1.Regime.Should().Be("Trending");
        bar1.ProposalCount.Should().Be(1);
        bar1.GateRejections.Should().ContainSingle()
            .Which.Should().Contain("BudgetBlocked");
        bar1.Verdicts.Should().HaveCount(2);
        bar1.Verdicts[0].StrategyId.Should().Be("test-s1");
        bar1.Verdicts[0].SignalFired.Should().BeTrue();
        bar1.Verdicts[1].StrategyId.Should().Be("test-s2");
        bar1.Verdicts[1].SignalFired.Should().BeFalse();
        bar1.Risk.Should().NotBeNull();
        bar1.Risk!.Equity.Should().Be(10_000m);
        bar1.FillCount.Should().Be(0);
        bar1.CloseCount.Should().Be(0);

        // ── Bar 2 (T2) ──
        var bar2 = bars[1];
        bar2.SimTimeUtc.Should().Be(new DateTime(2024, 1, 1, 0, 2, 0, DateTimeKind.Utc));
        bar2.Regime.Should().BeNull("no BarClosed with Regime in this bar");
        bar2.ProposalCount.Should().Be(0);
        bar2.Verdicts.Should().BeEmpty();
        bar2.FillCount.Should().Be(2);
        bar2.CloseCount.Should().Be(1);
        bar2.RejectionCount.Should().Be(1, "the FORCE close is not a gate rejection");
        bar2.Risk.Should().NotBeNull();
        bar2.Risk!.Equity.Should().Be(9_800m);
        bar2.Risk.OpenPositions.Should().Be(1);
    }
}
