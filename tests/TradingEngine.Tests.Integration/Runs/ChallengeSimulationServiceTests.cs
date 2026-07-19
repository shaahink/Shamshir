using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Tests.Integration.Support;
using TradingEngine.Web.Services;

namespace TradingEngine.Tests.Integration.Runs;

[Trait("Category", "Infrastructure")]
public sealed class ChallengeSimulationServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SqliteInMemory _db = new();

    private void SeedRiskProfileAndRuleSet(TradingDbContext ctx, string riskProfileId, string ruleSetId)
    {
        var profile = new RiskProfile(
            riskProfileId, riskProfileId, 0.005, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, ruleSetId);
        ctx.RiskProfiles.Add(new RiskProfileEntity
        {
            Id = riskProfileId,
            DisplayName = riskProfileId,
            Json = JsonSerializer.Serialize(profile, JsonOpts),
        });

        var ruleSet = new PropFirmRuleSet(
            ruleSetId, "FTMO Standard", "Fixed",
            0.05, 0.10, 0.10, 4,
            "BalancePlusFloatingMinusFeesAndSwaps", "22:00:00", "Europe/Prague",
            false, "High", 30, 15, false, "21:00:00", "20:00:00", "NextTradingDay", false);
        ctx.PropFirmRuleSets.Add(new PropFirmRuleSetEntity
        {
            Id = ruleSetId,
            DisplayName = "FTMO Standard",
            Json = JsonSerializer.Serialize(ruleSet, JsonOpts),
        });
        ctx.SaveChanges();
    }

    private void SeedRunWithEquityCurve(TradingDbContext ctx, string runId, string? riskProfileId, decimal[] dailyEquities)
    {
        ctx.BacktestRuns.Add(new BacktestRunEntity { RunId = runId, StartedAtUtc = DateTime.UtcNow, RiskProfileId = riskProfileId });

        var start = new DateTime(2026, 5, 6);
        var prevEquity = 100_000m;
        for (var i = 0; i < dailyEquities.Length; i++)
        {
            ctx.EquitySnapshots.Add(new EquitySnapshotEntity
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                TimestampUtc = start.AddDays(i).AddHours(10),
                DailyStartEquity = prevEquity,
                Equity = dailyEquities[i],
                Balance = dailyEquities[i],
                Mode = "Backtest",
            });
            prevEquity = dailyEquities[i];
        }
        ctx.SaveChanges();
    }

    [Fact]
    public async Task SimulateAsync_FallsBackToStandardRiskProfile_WhenRunHasNoOverride()
    {
        using (var ctx = _db.NewContext())
        {
            SeedRiskProfileAndRuleSet(ctx, "standard", "ftmo-standard");
            // 40 flat trading days — no target, no breach anywhere.
            SeedRunWithEquityCurve(ctx, "run-1", riskProfileId: null, Enumerable.Repeat(100_000m, 40).ToArray());
        }

        using var svcCtx = _db.NewContext();
        var svc = new ChallengeSimulationService(
            svcCtx,
            new SqliteEquityRepository(svcCtx),
            new SqliteRiskProfileStore(svcCtx, NullLogger<SqliteRiskProfileStore>.Instance),
            new SqlitePropFirmRuleSetStore(svcCtx, NullLogger<SqlitePropFirmRuleSetStore>.Instance));

        var result = await svc.SimulateAsync("run-1", windowCount: 3, windowDays: 30, CancellationToken.None);

        result.RuleSetId.Should().Be("ftmo-standard");
        result.Windows.Should().HaveCount(3);
        result.Windows.Should().OnlyContain(w => w.Verdict == ChallengeVerdict.Incomplete);
    }

    [Fact]
    public async Task SimulateAsync_ReportsPass_WhenAWindowReachesTargetWithEnoughTradingDays()
    {
        // +11% on day 1, held flat afterward — trades keep coming to satisfy MinTradingDays.
        var equities = Enumerable.Repeat(111_000m, 40).ToArray();

        using (var ctx = _db.NewContext())
        {
            SeedRiskProfileAndRuleSet(ctx, "aggressive", "ftmo-standard");
            SeedRunWithEquityCurve(ctx, "run-2", riskProfileId: "aggressive", equities);
            for (var i = 0; i < 4; i++)
            {
                ctx.Trades.Add(new TradeResultEntity
                {
                    Id = Guid.NewGuid(),
                    RunId = "run-2",
                    // V0: MinTradingDays counts days with a trade OPENED. The open must land
                    // inside the day's snapshot span (one snapshot at 10:00 here).
                    OpenedAtUtc = new DateTime(2026, 5, 6).AddDays(i).AddHours(10),
                    ClosedAtUtc = new DateTime(2026, 5, 6).AddDays(i).AddHours(10),
                });
            }
            ctx.SaveChanges();
        }

        using var svcCtx = _db.NewContext();
        var svc = new ChallengeSimulationService(
            svcCtx,
            new SqliteEquityRepository(svcCtx),
            new SqliteRiskProfileStore(svcCtx, NullLogger<SqliteRiskProfileStore>.Instance),
            new SqlitePropFirmRuleSetStore(svcCtx, NullLogger<SqlitePropFirmRuleSetStore>.Instance));

        var result = await svc.SimulateAsync("run-2", windowCount: 1, windowDays: 30, CancellationToken.None);

        result.Windows.Single().Verdict.Should().Be(ChallengeVerdict.Pass);
        result.PassRate.Should().Be(1.0);
    }

    private static EquitySnapshot Snap(DateTime ts, decimal dailyStart, decimal equity) =>
        new(ts, equity, 0, equity, equity, dailyStart, 0, 0, EngineMode.Backtest);

    [Fact]
    public void BuildDailyPoints_SplitsOnCalendarDate_EvenWhenDailyStartEquityNeverChanges()
    {
        // A genuinely flat multi-day stretch (no open position, identical DailyStartEquity every
        // reset) must still yield one bucket per calendar day — the DailyStartEquity-only check
        // would silently merge all three days into a single "trading day".
        var snaps = new[]
        {
            Snap(new DateTime(2026, 5, 6, 10, 0, 0), 100_000m, 100_000m),
            Snap(new DateTime(2026, 5, 7, 10, 0, 0), 100_000m, 100_000m),
            Snap(new DateTime(2026, 5, 8, 10, 0, 0), 100_000m, 100_000m),
        };

        var points = ChallengeSimulationService.BuildDailyPoints(snaps, Array.Empty<TradeResultEntity>());

        points.Should().HaveCount(3);
    }

    [Fact]
    public void BuildDailyPoints_GroupsMultipleSnapshotsPerDay_IntoOneBucket()
    {
        var snaps = new[]
        {
            Snap(new DateTime(2026, 5, 6, 1, 0, 0), 100_000m, 100_500m),
            Snap(new DateTime(2026, 5, 6, 5, 0, 0), 100_000m, 101_200m),
            Snap(new DateTime(2026, 5, 7, 1, 0, 0), 101_200m, 102_000m),
        };

        var points = ChallengeSimulationService.BuildDailyPoints(snaps, Array.Empty<TradeResultEntity>());

        points.Should().HaveCount(2);
        points[0].StartEquity.Should().Be(100_000m);
        points[0].EndEquity.Should().Be(101_200m);
        points[1].StartEquity.Should().Be(101_200m);
        points[1].EndEquity.Should().Be(102_000m);
    }

    [Fact]
    public void BuildDailyPoints_CountsTradesClosedWithinTheBucketsTimeRange()
    {
        var snaps = new[]
        {
            Snap(new DateTime(2026, 5, 6, 1, 0, 0), 100_000m, 100_000m),
            Snap(new DateTime(2026, 5, 6, 10, 0, 0), 100_000m, 100_500m),
        };
        var trades = new[]
        {
            new TradeResultEntity { ClosedAtUtc = new DateTime(2026, 5, 6, 5, 0, 0) },
            new TradeResultEntity { ClosedAtUtc = new DateTime(2026, 5, 7, 5, 0, 0) }, // outside the bucket
        };

        var points = ChallengeSimulationService.BuildDailyPoints(snaps, trades);

        points.Should().ContainSingle();
        points[0].TradesClosed.Should().Be(1);
    }

    [Fact]
    public void BuildDailyPoints_FillsTradesOpenedAndEndBalance()
    {
        var snaps = new[]
        {
            new EquitySnapshot(new DateTime(2026, 5, 6, 1, 0, 0), 100_200m, -200m, 100_000m, 100_200m, 100_000m, 0, 0, EngineMode.Backtest),
            new EquitySnapshot(new DateTime(2026, 5, 6, 10, 0, 0), 100_700m, -200m, 100_500m, 100_700m, 100_000m, 0, 0, EngineMode.Backtest),
        };
        var trades = new[]
        {
            new TradeResultEntity { OpenedAtUtc = new DateTime(2026, 5, 6, 5, 0, 0), ClosedAtUtc = new DateTime(2026, 5, 7, 5, 0, 0) },
            new TradeResultEntity { OpenedAtUtc = new DateTime(2026, 5, 5, 5, 0, 0), ClosedAtUtc = new DateTime(2026, 5, 6, 5, 0, 0) }, // opened before the bucket
        };

        var points = ChallengeSimulationService.BuildDailyPoints(snaps, trades);

        points.Should().ContainSingle();
        points[0].TradesOpened.Should().Be(1);
        points[0].TradesClosed.Should().Be(1);
        points[0].EndBalance.Should().Be(100_700m); // closed-only balance, not equity
    }

    // V0 rule truth: FTMO evaluations are untimed — anchored windows run to the end of history;
    // unresolved ones are CENSORED (not failed), P(bust) conditions on resolved windows only,
    // and time-to-target is calendar days inclusive of both endpoints.
    [Fact]
    public void ComputeUntimedMetrics_SeparatesPassesBustsAndCensored()
    {
        var ruleSet = new PropFirmRuleSet(
            "ftmo-standard", "FTMO Standard", "Fixed",
            0.05, 0.10, 0.10, 4,
            "BalancePlusFloatingMinusFeesAndSwaps", "00:00:00", "Europe/Prague",
            true, "High", 2, 2, true, "21:00:00", "20:00:00", "NextTradingDay", false);

        DailyEquityPoint D(int offset, decimal start, decimal end) =>
            new(new DateTime(2026, 5, 6).AddDays(offset), start, end, 1, 1);

        var days = new[]
        {
            D(0, 100_000m, 102_000m),
            D(1, 102_000m, 104_000m),
            D(2, 104_000m, 107_000m),
            D(3, 107_000m, 110_500m), // anchor 0 hits +10% here on its 4th trading day → Pass
            D(4, 110_500m, 104_000m), // floor 110,500 − 5% of each anchor's capital → bust for anchors 1..4
        };

        var (pBust, eTime, medTime, passes, busts, censored) =
            ChallengeSimulationService.ComputeUntimedMetrics(days, ruleSet);

        passes.Should().Be(1);
        busts.Should().Be(4);
        censored.Should().Be(0);
        pBust.Should().BeApproximately(0.8, 1e-9);
        eTime.Should().BeApproximately(4.0, 1e-9);   // 2026-05-06 → 2026-05-09 inclusive
        medTime.Should().BeApproximately(4.0, 1e-9);

        // A flat tail with no trades resolves nothing — censored, never failed.
        var flat = Enumerable.Range(0, 5)
            .Select(i => new DailyEquityPoint(new DateTime(2026, 5, 6).AddDays(i), 100_000m, 100_000m, 0, 0))
            .ToArray();
        var flatMetrics = ChallengeSimulationService.ComputeUntimedMetrics(flat, ruleSet);
        flatMetrics.Censored.Should().Be(5);
        flatMetrics.PBust.Should().BeNull();
        flatMetrics.ETimeDays.Should().BeNull();
    }

    [Fact]
    public void BuildRollingWindows_SpreadsStartsEvenlyFromZeroToMaxStart()
    {
        var days = Enumerable.Range(0, 60)
            .Select(i => new DailyEquityPoint(new DateTime(2026, 5, 6).AddDays(i), 100_000m, 100_000m, 1))
            .ToList();

        var windows = ChallengeSimulationService.BuildRollingWindows(days, windowCount: 3, windowDays: 30);

        windows.Should().HaveCount(3);
        windows.Select(w => w.Count).Should().AllBeEquivalentTo(30);
        windows[0][0].Date.Should().Be(days[0].Date);
        windows[1][0].Date.Should().Be(days[15].Date); // maxStart=30, midpoint offset=15
        windows[2][0].Date.Should().Be(days[30].Date);
    }

    [Fact]
    public void BuildRollingWindows_ClampsWindowSize_WhenFewerDaysThanRequested()
    {
        var days = Enumerable.Range(0, 10)
            .Select(i => new DailyEquityPoint(new DateTime(2026, 5, 6).AddDays(i), 100_000m, 100_000m, 1))
            .ToList();

        var windows = ChallengeSimulationService.BuildRollingWindows(days, windowCount: 3, windowDays: 30);

        windows.Should().HaveCount(3);
        windows.Select(w => w.Count).Should().AllBeEquivalentTo(10);
    }

    public void Dispose() => _db.Dispose();
}
