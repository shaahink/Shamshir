using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.Runs;

[Trait("Category", "Infrastructure")]
public sealed class DeleteRunsCascadeTests : IDisposable
{
    private readonly SqliteInMemory _db = new();

    private void Seed(string runId)
    {
        using var ctx = _db.NewContext();
        ctx.BacktestRuns.Add(new BacktestRunEntity { RunId = runId, StartedAtUtc = DateTime.UtcNow });
        ctx.Trades.Add(new TradeResultEntity { Id = Guid.NewGuid(), RunId = runId });
        ctx.JournalEntries.Add(new JournalEntryEntity { RunId = runId, Seq = 1, EventKind = "BarClosed" });
        ctx.EquitySnapshots.Add(new EquitySnapshotEntity { Id = Guid.NewGuid(), RunId = runId });
        ctx.Bars.Add(new BarEntity { Id = Guid.NewGuid(), RunId = runId, Symbol = "EURUSD", Timeframe = "H1" });
        ctx.VenueSessions.Add(new VenueSessionEntity { Id = Guid.NewGuid(), RunId = runId, Event = "open" });
        ctx.SaveChanges();
    }

    private (int runs, int trades, int journal, int equity, int bars, int sessions) CountsFor(string runId)
    {
        using var ctx = _db.NewContext();
        return (
            ctx.BacktestRuns.Count(r => r.RunId == runId),
            ctx.Trades.Count(t => t.RunId == runId),
            ctx.JournalEntries.Count(j => j.RunId == runId),
            ctx.EquitySnapshots.Count(e => e.RunId == runId),
            ctx.Bars.Count(b => b.RunId == runId),
            ctx.VenueSessions.Count(v => v.RunId == runId));
    }

    [Fact]
    public async Task DeleteRuns_removesEveryRunScopedRow_forTargetOnly()
    {
        Seed("run-A");
        Seed("run-B");

        int deleted;
        using (var ctx = _db.NewContext())
        {
            var repo = new SqliteBacktestRunRepository(ctx);
            deleted = await repo.DeleteRunsAsync(new[] { "run-A" }, CancellationToken.None);
        }

        Assert.Equal(1, deleted);

        var a = CountsFor("run-A");
        Assert.Equal((0, 0, 0, 0, 0, 0), a);

        var b = CountsFor("run-B");
        Assert.Equal((1, 1, 1, 1, 1, 1), b);
    }

    [Fact]
    public async Task DeleteRuns_emptyInput_isNoOp()
    {
        Seed("run-A");
        using var ctx = _db.NewContext();
        var repo = new SqliteBacktestRunRepository(ctx);

        var deleted = await repo.DeleteRunsAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(0, deleted);
        Assert.Equal((1, 1, 1, 1, 1, 1), CountsFor("run-A"));
    }

    public void Dispose() => _db.Dispose();
}
