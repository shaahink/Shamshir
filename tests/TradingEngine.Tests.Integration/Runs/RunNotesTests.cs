using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.Runs;

// X2 — run notes through the REAL persistence path. The load-bearing property: notes are written
// ONLY via SetNotesAsync, so the run-lifecycle writers (SaveAsync start record, UpdateAsync end
// record) can never clobber a note typed while the run was still going.
[Trait("Category", "Infrastructure")]
public sealed class RunNotesTests : IDisposable
{
    private readonly SqliteInMemory _db = new();

    private static BacktestRunSummary Base(string runId) => new(
        runId, DateTime.UtcNow.AddMinutes(-5), DateTime.MinValue,
        "EURUSD", "H1", "[\"EURUSD\"]", "[\"H1\"]", DateTime.UtcNow.AddDays(-7), DateTime.UtcNow,
        100_000m, "algo", "{}", "{}",
        0, 0, 0, 0, 0, 0, 0, 0, -1, null);

    [Fact]
    public async Task SetNotes_RoundTrips()
    {
        using (var ctx = _db.NewContext())
        {
            var repo = new SqliteBacktestRunRepository(ctx);
            await repo.SaveAsync(Base("run-notes"), CancellationToken.None);
            await repo.SetNotesAsync("run-notes", "promising cell — rerun with pack B", CancellationToken.None);
        }
        using (var ctx = _db.NewContext())
        {
            var repo = new SqliteBacktestRunRepository(ctx);
            var read = await repo.GetByIdAsync("run-notes", CancellationToken.None);
            read!.Notes.Should().Be("promising cell — rerun with pack B");
        }
    }

    [Fact]
    public async Task EndRecordUpdate_DoesNotClobber_ANoteTypedMidRun()
    {
        using (var ctx = _db.NewContext())
        {
            var repo = new SqliteBacktestRunRepository(ctx);
            await repo.SaveAsync(Base("run-clobber"), CancellationToken.None);
            // Owner types a note while the run is still going...
            await repo.SetNotesAsync("run-clobber", "watching this one", CancellationToken.None);
            // ...then the run finishes and the orchestrator writes its end record (a summary
            // built from run state, which knows nothing about notes).
            var end = Base("run-clobber") with
            {
                CompletedAtUtc = DateTime.UtcNow,
                ExitCode = 0,
                TotalTrades = 5,
                NetProfit = 123.45m,
                Status = "completed",
            };
            await repo.UpdateAsync(end, CancellationToken.None);
        }
        using (var ctx = _db.NewContext())
        {
            var repo = new SqliteBacktestRunRepository(ctx);
            var read = await repo.GetByIdAsync("run-clobber", CancellationToken.None);
            read!.Notes.Should().Be("watching this one", "the end-record write must not erase the note");
            read.TotalTrades.Should().Be(5, "the end record itself must still land");
        }
    }

    [Fact]
    public async Task SetNotes_Whitespace_ClearsToNull()
    {
        using (var ctx = _db.NewContext())
        {
            var repo = new SqliteBacktestRunRepository(ctx);
            await repo.SaveAsync(Base("run-clear"), CancellationToken.None);
            await repo.SetNotesAsync("run-clear", "temp", CancellationToken.None);
            await repo.SetNotesAsync("run-clear", "   ", CancellationToken.None);
        }
        using (var ctx = _db.NewContext())
        {
            var repo = new SqliteBacktestRunRepository(ctx);
            var read = await repo.GetByIdAsync("run-clear", CancellationToken.None);
            read!.Notes.Should().BeNull();
        }
    }

    public void Dispose() => _db.Dispose();
}
