using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Tests.Integration.Support;
using TradingEngine.Web.Services;

namespace TradingEngine.Tests.Integration.Experiments;

// D13 (iter-alpha-loop PLAN §0b): one cell = one run, and a below-floor/invalid cell persists an
// ExperimentRun row with a NULL score and the reason. F5's root cause was the opposite — validity
// failures returned early without a row, so R1's census (gate: >= 225 ExperimentRuns) could only
// ever see the 4 above-floor cells. These tests pin the persistence contract R1' depends on.
[Trait("Category", "Infrastructure")]
public sealed class SetupScorePersistenceTests : IDisposable
{
    private readonly SqliteInMemory _db = new();

    private static BacktestRunEntity Run(
        string id, string venue = "tape", string? warnings = null, string status = "completed",
        bool finished = true, decimal maxDrawdownPct = 0.02m) => new()
    {
        RunId = id,
        StartedAtUtc = DateTime.UtcNow.AddHours(-2),
        CompletedAtUtc = finished ? DateTime.UtcNow.AddHours(-1) : DateTime.MinValue,
        Status = status,
        Venue = venue,
        WarningsJson = warnings,
        BacktestFrom = DateTime.UtcNow.AddDays(-90),
        BacktestTo = DateTime.UtcNow,
        MaxDrawdownPct = maxDrawdownPct, // stored as a FRACTION (0.02 = 2%) — see F60
        Symbol = "EURUSD",
        Period = "H1",
    };

    private static TradeResultEntity Trade(string runId, double r, decimal netPnl, DateTime closedAt) => new()
    {
        Id = Guid.NewGuid(),
        RunId = runId,
        StrategyId = "trend-breakout",
        Symbol = "EURUSD",
        Direction = "Buy",
        RMultiple = r,
        NetPnLAmount = netPnl,
        OpenedAtUtc = closedAt.AddHours(-4),
        ClosedAtUtc = closedAt,
    };

    private async Task SeedTradesAsync(string runId, int count, double r = 0.4)
    {
        using var ctx = _db.NewContext();
        var t0 = DateTime.UtcNow.AddDays(-60);
        for (var i = 0; i < count; i++)
            ctx.Trades.Add(Trade(runId, r, 25m, t0.AddDays(i)));
        await ctx.SaveChangesAsync();
    }

    private async Task<ScoreResult> ScoreAsync(string runId)
    {
        using var ctx = _db.NewContext();
        var svc = new SetupScoreService(ctx, NullLogger<SetupScoreService>.Instance);
        return await svc.ScoreRunAsync(runId, null, null, null, null, CancellationToken.None);
    }

    [Fact]
    public async Task BelowFloor_PersistsNullScore_WithReason()
    {
        using (var ctx = _db.NewContext())
        {
            ctx.BacktestRuns.Add(Run("cell-below-floor"));
            await ctx.SaveChangesAsync();
        }
        await SeedTradesAsync("cell-below-floor", 3);

        var result = await ScoreAsync("cell-below-floor");

        result.Passed.Should().BeFalse();
        result.Reason.Should().Contain("below floor");

        using var read = _db.NewContext();
        var row = read.ExperimentRuns.Single(er => er.BacktestRunId == "cell-below-floor");
        using var doc = JsonDocument.Parse(row.ScoreJson);
        doc.RootElement.GetProperty("Composite").ValueKind.Should().Be(JsonValueKind.Null,
            "a below-floor cell is a census result, not a missing one (D13)");
        doc.RootElement.GetProperty("NullReason").GetString().Should().Contain("below floor");
        doc.RootElement.GetProperty("Trades").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task NonTapeVenue_PersistsNullScore_WithReason()
    {
        using (var ctx = _db.NewContext())
        {
            ctx.BacktestRuns.Add(Run("cell-ctrader", venue: "ctrader"));
            await ctx.SaveChangesAsync();
        }

        var result = await ScoreAsync("cell-ctrader");

        result.Passed.Should().BeFalse();
        using var read = _db.NewContext();
        var row = read.ExperimentRuns.Single(er => er.BacktestRunId == "cell-ctrader");
        using var doc = JsonDocument.Parse(row.ScoreJson);
        doc.RootElement.GetProperty("Composite").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("NullReason").GetString().Should().Contain("tape");
    }

    [Fact]
    public async Task UnfinishedRun_PersistsNullScore_WithStatusReason()
    {
        using (var ctx = _db.NewContext())
        {
            // Legacy shape: no persisted Status, not finished — resolver derives "running".
            ctx.BacktestRuns.Add(Run("cell-running", status: "", finished: false));
            await ctx.SaveChangesAsync();
        }

        var result = await ScoreAsync("cell-running");

        result.Passed.Should().BeFalse();
        result.Reason.Should().Contain("not completed");
        using var read = _db.NewContext();
        read.ExperimentRuns.Should().ContainSingle(er => er.BacktestRunId == "cell-running");
    }

    [Fact]
    public async Task AboveFloor_PersistsScoredRow()
    {
        using (var ctx = _db.NewContext())
        {
            ctx.BacktestRuns.Add(Run("cell-scored"));
            await ctx.SaveChangesAsync();
        }
        await SeedTradesAsync("cell-scored", 25);

        var result = await ScoreAsync("cell-scored");

        result.Passed.Should().BeTrue();
        using var read = _db.NewContext();
        var row = read.ExperimentRuns.Single(er => er.BacktestRunId == "cell-scored");
        using var doc = JsonDocument.Parse(row.ScoreJson);
        doc.RootElement.GetProperty("Composite").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task Rescore_Upserts_NullToScored_WithoutDuplicating()
    {
        using (var ctx = _db.NewContext())
        {
            ctx.BacktestRuns.Add(Run("cell-upsert"));
            await ctx.SaveChangesAsync();
        }
        await SeedTradesAsync("cell-upsert", 3);
        (await ScoreAsync("cell-upsert")).Passed.Should().BeFalse();

        // The cell later re-runs long enough to clear the floor; re-scoring must upgrade the SAME row.
        await SeedTradesAsync("cell-upsert", 22);
        (await ScoreAsync("cell-upsert")).Passed.Should().BeTrue();

        using var read = _db.NewContext();
        var rows = read.ExperimentRuns.Where(er => er.BacktestRunId == "cell-upsert").ToList();
        rows.Should().HaveCount(1, "re-scoring a cell must upsert, not append");
        using var doc = JsonDocument.Parse(rows[0].ScoreJson);
        doc.RootElement.GetProperty("Composite").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task Scoreboard_Counts_ScoredAndNull_Separately()
    {
        using (var ctx = _db.NewContext())
        {
            ctx.BacktestRuns.Add(Run("cell-a"));
            ctx.BacktestRuns.Add(Run("cell-b"));
            await ctx.SaveChangesAsync();
        }
        await SeedTradesAsync("cell-a", 25);
        await SeedTradesAsync("cell-b", 2);
        await ScoreAsync("cell-a");
        await ScoreAsync("cell-b");

        using var ctx2 = _db.NewContext();
        var svc = new SetupScoreService(ctx2, NullLogger<SetupScoreService>.Instance);
        var experimentId = ctx2.Experiments.Single().Id;
        var board = await svc.GetScoreboardAsync(experimentId, top: 20, CancellationToken.None);

        board.TotalRuns.Should().Be(2);
        board.ScoredRuns.Should().Be(1);
        board.NullRuns.Should().Be(1, "the R1' truth gate counts scored-or-null coverage");
        board.Top.Should().ContainSingle(e => e.BacktestRunId == "cell-a");
    }

    // F60: BacktestRuns.MaxDrawdownPct is a FRACTION, but ComputeDrawdownScore's thresholds
    // (<=3 -> 100, >=10 -> 0) are percent-scaled. Before the fix, every real run's stored value
    // (0.014..0.116) read as "<=3" and saturated Drawdown=100 regardless of actual risk taken.
    [Fact]
    public async Task DrawdownScore_ReadsStoredFractionAsPercent()
    {
        using (var ctx = _db.NewContext())
        {
            ctx.BacktestRuns.Add(Run("cell-low-dd", maxDrawdownPct: 0.014m)); // 1.4%
            ctx.BacktestRuns.Add(Run("cell-high-dd", maxDrawdownPct: 0.116m)); // 11.6%
            await ctx.SaveChangesAsync();
        }
        await SeedTradesAsync("cell-low-dd", 25);
        await SeedTradesAsync("cell-high-dd", 25);

        var low = await ScoreAsync("cell-low-dd");
        var high = await ScoreAsync("cell-high-dd");

        using var lowDoc = JsonDocument.Parse(low.ScoreJson);
        using var highDoc = JsonDocument.Parse(high.ScoreJson);
        var lowComponents = lowDoc.RootElement.GetProperty("Components");
        var highComponents = highDoc.RootElement.GetProperty("Components");

        lowComponents.GetProperty("Drawdown").GetDouble().Should().Be(100, "1.4% is well under the 3% ceiling");
        highComponents.GetProperty("Drawdown").GetDouble().Should().Be(0, "11.6% is over the 10% floor");

        lowComponents.GetProperty("DrawdownPct").GetDouble().Should().BeApproximately(1.4, 0.01);
        highComponents.GetProperty("DrawdownPct").GetDouble().Should().BeApproximately(11.6, 0.01);
    }

    // F62: SetupScoreService.ScoreRunAsync hardcoded oosRatio=null unconditionally — the plan's
    // "walk-forward upgrades sv1-partial to full sv1" step had nothing to compute it from. These
    // pin the fix: Walk-Forward Efficiency = sum(TestNetProfit) / sum(PlateauValue) across a job's
    // windows (Pardo's standard walk-forward methodology).
    private async Task SeedWalkForwardJobAsync(Guid jobId, params (decimal testNetProfit, double? plateauValue)[] windows)
    {
        using var ctx = _db.NewContext();
        ctx.WalkForwardJobs.Add(new WalkForwardJobEntity { Id = jobId, Status = "completed" });
        var i = 0;
        foreach (var (testNetProfit, plateauValue) in windows)
        {
            ctx.WalkForwardWindowResults.Add(new WalkForwardWindowResultEntity
            {
                Id = Guid.NewGuid(),
                JobId = jobId,
                WindowIndex = i++,
                StrategyId = "trend-breakout",
                Symbol = "EURUSD",
                Timeframe = "H1",
                TestNetProfit = testNetProfit,
                PlateauValue = plateauValue,
            });
        }
        await ctx.SaveChangesAsync();
    }

    private async Task<ScoreResult> ScoreWithWalkForwardAsync(string runId, Guid walkForwardJobId)
    {
        using var ctx = _db.NewContext();
        var svc = new SetupScoreService(ctx, NullLogger<SetupScoreService>.Instance);
        return await svc.ScoreRunAsync(runId, null, null, null, null, CancellationToken.None,
            walkForwardJobId: walkForwardJobId);
    }

    [Fact]
    public async Task WalkForwardJob_ComputesOosRatio_UpgradesToFullSv1()
    {
        using (var ctx = _db.NewContext())
        {
            ctx.BacktestRuns.Add(Run("cell-wf-good"));
            await ctx.SaveChangesAsync();
        }
        await SeedTradesAsync("cell-wf-good", 25);

        var jobId = Guid.NewGuid();
        // 3 folds: train (Plateau) profit 1000/1000/1000 = 3000; test profit 700/500/600 = 1800.
        // WFE = 1800/3000 = 0.6 -> RobustnessOos = 60.
        await SeedWalkForwardJobAsync(jobId, (700m, 1000), (500m, 1000), (600m, 1000));

        var result = await ScoreWithWalkForwardAsync("cell-wf-good", jobId);

        result.Passed.Should().BeTrue();
        result.Version.Should().Be("sv1", "a real OOS ratio upgrades the cell out of sv1-partial");
        using var doc = JsonDocument.Parse(result.ScoreJson);
        doc.RootElement.GetProperty("VersionKind").GetString().Should().Be("sv1");
        var components = doc.RootElement.GetProperty("Components");
        components.GetProperty("OosRatio").GetDouble().Should().BeApproximately(0.6, 0.001);
        components.GetProperty("RobustnessOos").GetDouble().Should().BeApproximately(60, 0.1);
    }

    [Fact]
    public async Task WalkForwardJob_OosRatioAboveOne_ClampsRobustnessScoreAt100()
    {
        using (var ctx = _db.NewContext())
        {
            ctx.BacktestRuns.Add(Run("cell-wf-great"));
            await ctx.SaveChangesAsync();
        }
        await SeedTradesAsync("cell-wf-great", 25);

        var jobId = Guid.NewGuid();
        // Test outperformed train: WFE = 2000/1000 = 2.0 -> RobustnessOos clamps to 100, not 200.
        await SeedWalkForwardJobAsync(jobId, (2000m, 1000));

        var result = await ScoreWithWalkForwardAsync("cell-wf-great", jobId);

        using var doc = JsonDocument.Parse(result.ScoreJson);
        var components = doc.RootElement.GetProperty("Components");
        components.GetProperty("OosRatio").GetDouble().Should().BeApproximately(2.0, 0.001);
        components.GetProperty("RobustnessOos").GetDouble().Should().Be(100);
    }

    [Fact]
    public async Task WalkForwardJob_NonPositiveInSampleProfit_ScoresOosZero_NotNull()
    {
        using (var ctx = _db.NewContext())
        {
            ctx.BacktestRuns.Add(Run("cell-wf-losing-is"));
            await ctx.SaveChangesAsync();
        }
        await SeedTradesAsync("cell-wf-losing-is", 25);

        var jobId = Guid.NewGuid();
        // The chosen in-sample params were themselves net-losing (train sum <= 0) — there is no
        // genuine in-sample edge to measure OOS efficiency against. Score 0, not null: a silent
        // null would leave this looking like "no walk-forward data" instead of "walk-forward failed".
        await SeedWalkForwardJobAsync(jobId, (500m, -200), (-100m, -100));

        var result = await ScoreWithWalkForwardAsync("cell-wf-losing-is", jobId);

        result.Version.Should().Be("sv1", "oosRatio has a value (0), so this is still a full score");
        using var doc = JsonDocument.Parse(result.ScoreJson);
        var components = doc.RootElement.GetProperty("Components");
        components.GetProperty("OosRatio").GetDouble().Should().Be(0);
        components.GetProperty("RobustnessOos").GetDouble().Should().Be(0);
    }

    [Fact]
    public async Task NoWalkForwardJob_LeavesOosRatioNull_StaysSv1Partial()
    {
        using (var ctx = _db.NewContext())
        {
            ctx.BacktestRuns.Add(Run("cell-no-wf"));
            await ctx.SaveChangesAsync();
        }
        await SeedTradesAsync("cell-no-wf", 25);

        var result = await ScoreAsync("cell-no-wf");

        result.Version.Should().Be("sv1-partial", "without a walk-forward job there is nothing to compute OOS from");
        using var doc = JsonDocument.Parse(result.ScoreJson);
        doc.RootElement.GetProperty("Components").GetProperty("OosRatio").ValueKind.Should().Be(JsonValueKind.Null);
    }

    public void Dispose() => _db.Dispose();
}
