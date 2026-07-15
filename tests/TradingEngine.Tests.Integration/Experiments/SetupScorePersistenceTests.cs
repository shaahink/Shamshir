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
        bool finished = true) => new()
    {
        RunId = id,
        StartedAtUtc = DateTime.UtcNow.AddHours(-2),
        CompletedAtUtc = finished ? DateTime.UtcNow.AddHours(-1) : DateTime.MinValue,
        Status = status,
        Venue = venue,
        WarningsJson = warnings,
        BacktestFrom = DateTime.UtcNow.AddDays(-90),
        BacktestTo = DateTime.UtcNow,
        MaxDrawdownPct = 2m,
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

    public void Dispose() => _db.Dispose();
}
