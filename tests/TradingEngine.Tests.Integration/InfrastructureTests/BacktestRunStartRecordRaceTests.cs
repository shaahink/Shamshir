using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.InfrastructureTests;

// L1 (god-classes SURVEY, observed live): EnqueueRun writes a "queued" start record and RunAsync
// writes a second start record. When the queued INSERT lands between RunAsync's Find and its
// SaveChanges, the UNIQUE index on BacktestRuns.RunId fires and the Status upgrade
// (queued -> running) used to be silently lost behind a warning. SaveAsync must recover by
// re-applying the write as an update against the row that won the race.
[Trait("Category", "Infrastructure")]
public sealed class BacktestRunStartRecordRaceTests : IDisposable
{
    private readonly SqliteInMemory _db = new();

    /// <summary>
    /// Reproduces the race deterministically: just before the outer context's INSERT commits,
    /// a second writer (a plain context on the same connection, no interceptor) inserts the same
    /// RunId — exactly the queued-record writer winning the race.
    /// </summary>
    private sealed class RivalInsertInterceptor(DbContextOptions<TradingDbContext> plainOptions, string runId)
        : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!_fired)
            {
                _fired = true;
                using var rival = new TradingDbContext(plainOptions);
                rival.BacktestRuns.Add(new BacktestRunEntity
                {
                    RunId = runId,
                    StartedAtUtc = DateTime.UtcNow,
                    Status = "queued",
                });
                await rival.SaveChangesAsync(ct);
            }
            return result;
        }
    }

    [Fact]
    public async Task SaveAsync_LosingTheInsertRace_RecoversAndAppliesTheStatusUpgrade()
    {
        const string runId = "race-run-1";
        var racingOptions = new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlite(_db.Connection)
            .AddInterceptors(new RivalInsertInterceptor(_db.Options, runId))
            .Options;

        using (var ctx = new TradingDbContext(racingOptions))
        {
            var repo = new SqliteBacktestRunRepository(ctx);
            var running = new BacktestRunSummary(
                runId, DateTime.UtcNow, DateTime.MinValue, "EURUSD", "h1", "EURUSD", "h1",
                new DateTime(2026, 3, 1), new DateTime(2026, 5, 1), 100_000m,
                "hash", "{}", null, 0, 0, 0, 0, 0, 0, 0, 0, 0, null,
                Status: "running");

            await repo.SaveAsync(running, CancellationToken.None);
        }

        using var verify = _db.NewContext();
        var rows = await verify.BacktestRuns.Where(r => r.RunId == runId).ToListAsync();
        rows.Should().ContainSingle("the race must not duplicate the run");
        rows[0].Status.Should().Be("running", "the status upgrade must survive losing the insert race");
    }

    public void Dispose() => _db.Dispose();
}
