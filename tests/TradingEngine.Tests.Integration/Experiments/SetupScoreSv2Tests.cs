using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Tests.Integration.Support;
using TradingEngine.Web.Services;

namespace TradingEngine.Tests.Integration.Experiments;

// iter-structural-edge S0 (F63 executed / D4): sv2's FtmoSurvival is ChallengeSimulator-backed —
// rolling 30-day windows over the run's REAL daily equity, FTMO-standard semantics — replacing the
// sv1 placeholder that only eyeballed 10% dips from peak. These pin the plan's G0 contract:
// an equity path passing 0/N windows scores 0; N/N scores 1 (FtmoSurvival 100, PassRate 1.0);
// un-computable survival (no snapshots) stays NULL so the composite renormalizes without it.
[Trait("Category", "Infrastructure")]
public sealed class SetupScoreSv2Tests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly SqliteInMemory _db = new();

    private static BacktestRunEntity Run(string id) => new()
    {
        RunId = id,
        StartedAtUtc = DateTime.UtcNow.AddHours(-2),
        CompletedAtUtc = DateTime.UtcNow.AddHours(-1),
        Status = "completed",
        Venue = "tape",
        BacktestFrom = new DateTime(2026, 3, 1),
        BacktestTo = new DateTime(2026, 5, 1),
        MaxDrawdownPct = 0.02m,
        Symbol = "EURUSD",
        Period = "H1",
    };

    private void SeedRiskProfileAndRuleSet(TradingDbContext ctx)
    {
        var profile = new RiskProfile(
            "standard", "standard", 0.005, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 3, false, "ftmo-standard");
        ctx.RiskProfiles.Add(new RiskProfileEntity
        {
            Id = "standard",
            DisplayName = "standard",
            Json = JsonSerializer.Serialize(profile, JsonOpts),
        });
        var ruleSet = new PropFirmRuleSet(
            "ftmo-standard", "FTMO Standard", "Fixed",
            0.05, 0.10, 0.10, 4,
            "BalancePlusFloatingMinusFeesAndSwaps", "22:00:00", "Europe/Prague",
            false, "High", 30, 15, false, "21:00:00", "20:00:00", "NextTradingDay", false);
        ctx.PropFirmRuleSets.Add(new PropFirmRuleSetEntity
        {
            Id = "ftmo-standard",
            DisplayName = "FTMO Standard",
            Json = JsonSerializer.Serialize(ruleSet, JsonOpts),
        });
    }

    /// <summary>
    /// One snapshot + one closed trade per day (the trade timestamp inside the day's bucket, so
    /// every day counts toward MinTradingDays). <paramref name="equityOfDay"/> gives day i's
    /// closing equity; DailyStartEquity is the previous day's close.
    /// </summary>
    private void SeedRunWithDailyCurve(string runId, int days, Func<int, decimal> equityOfDay)
    {
        using var ctx = _db.NewContext();
        SeedRiskProfileAndRuleSet(ctx);
        ctx.BacktestRuns.Add(Run(runId));
        var start = new DateTime(2026, 3, 1, 10, 0, 0);
        var prev = 100_000m;
        for (var i = 0; i < days; i++)
        {
            var ts = start.AddDays(i);
            var equity = equityOfDay(i);
            ctx.EquitySnapshots.Add(new EquitySnapshotEntity
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                TimestampUtc = ts,
                DailyStartEquity = prev,
                Equity = equity,
                Balance = equity,
                Mode = "Backtest",
            });
            ctx.Trades.Add(new TradeResultEntity
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                StrategyId = "trend-breakout",
                Symbol = "EURUSD",
                Direction = "Buy",
                RMultiple = 0.4,
                NetPnLAmount = equity - prev,
                OpenedAtUtc = ts.AddHours(-4),
                ClosedAtUtc = ts,
            });
            prev = equity;
        }
        ctx.SaveChanges();
    }

    private async Task<ScoreResult> ScoreAsync(string runId)
    {
        using var ctx = _db.NewContext();
        var challengeSim = new ChallengeSimulationService(
            ctx,
            new SqliteEquityRepository(ctx),
            new SqliteRiskProfileStore(ctx, NullLogger<SqliteRiskProfileStore>.Instance),
            new SqlitePropFirmRuleSetStore(ctx, NullLogger<SqlitePropFirmRuleSetStore>.Instance));
        var svc = new SetupScoreService(ctx, challengeSim, NullLogger<SetupScoreService>.Instance);
        return await svc.ScoreRunAsync(runId, null, null, null, null, CancellationToken.None);
    }

    [Fact]
    public async Task FlatEquity_PassesZeroWindows_SurvivalScoresZero_NotNull()
    {
        // 60 flat days -> 31 rolling 30-day windows, every one Incomplete. 0/N must score 0:
        // an Incomplete is a non-pass (R4's velocity failure), not missing data.
        SeedRunWithDailyCurve("run-flat", 60, _ => 100_000m);

        var result = await ScoreAsync("run-flat");

        result.Passed.Should().BeTrue();
        result.Version.Should().Be("sv2-partial");
        using var doc = JsonDocument.Parse(result.ScoreJson);
        var components = doc.RootElement.GetProperty("Components");
        components.GetProperty("FtmoSurvival").GetDouble().Should().Be(0);
        components.GetProperty("FtmoPassRate").GetDouble().Should().Be(0);
        components.GetProperty("FtmoWindows").GetInt32().Should().Be(31);
        components.GetProperty("FtmoPasses").GetInt32().Should().Be(0);
        components.GetProperty("FtmoIncompletes").GetInt32().Should().Be(31);
        components.GetProperty("FtmoRuleSetId").GetString().Should().Be("ftmo-standard");
    }

    [Fact]
    public async Task CompoundingEquity_PassesEveryWindow_SurvivalScoresOne()
    {
        // +1%/day compounding: every 30-day window gains ~34.8% (>= the 10% target by ~day 10),
        // never has a losing day, and closes a trade daily (MinTradingDays met). N/N scores 1.
        SeedRunWithDailyCurve("run-compound", 60,
            i => Math.Round(100_000m * (decimal)Math.Pow(1.01, i + 1), 2));

        var result = await ScoreAsync("run-compound");

        result.Passed.Should().BeTrue();
        using var doc = JsonDocument.Parse(result.ScoreJson);
        var components = doc.RootElement.GetProperty("Components");
        components.GetProperty("FtmoSurvival").GetDouble().Should().Be(100);
        components.GetProperty("FtmoPassRate").GetDouble().Should().Be(1.0);
        components.GetProperty("FtmoPasses").GetInt32().Should().Be(components.GetProperty("FtmoWindows").GetInt32());
    }

    [Fact]
    public async Task NoEquitySnapshots_LeavesSurvivalNull_CompositeRenormalizes()
    {
        // Scoreable run (25 trades) but no equity path at all: survival must be NULL — the
        // composite skips the component rather than punishing the cell with a fake 0.
        using (var ctx = _db.NewContext())
        {
            SeedRiskProfileAndRuleSet(ctx);
            ctx.BacktestRuns.Add(Run("run-no-equity"));
            var t0 = new DateTime(2026, 3, 2, 10, 0, 0);
            for (var i = 0; i < 25; i++)
            {
                ctx.Trades.Add(new TradeResultEntity
                {
                    Id = Guid.NewGuid(),
                    RunId = "run-no-equity",
                    StrategyId = "trend-breakout",
                    Symbol = "EURUSD",
                    Direction = "Buy",
                    RMultiple = 0.4,
                    NetPnLAmount = 25m,
                    OpenedAtUtc = t0.AddDays(i).AddHours(-4),
                    ClosedAtUtc = t0.AddDays(i),
                });
            }
            ctx.SaveChanges();
        }

        var result = await ScoreAsync("run-no-equity");

        result.Passed.Should().BeTrue();
        result.Version.Should().Be("sv2-partial");
        using var doc = JsonDocument.Parse(result.ScoreJson);
        doc.RootElement.GetProperty("Version").GetString().Should().Be("sv2");
        var components = doc.RootElement.GetProperty("Components");
        components.GetProperty("FtmoSurvival").ValueKind.Should().Be(JsonValueKind.Null);
        components.GetProperty("FtmoWindows").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("Composite").ValueKind.Should().Be(JsonValueKind.Number);
    }

    public void Dispose() => _db.Dispose();
}
