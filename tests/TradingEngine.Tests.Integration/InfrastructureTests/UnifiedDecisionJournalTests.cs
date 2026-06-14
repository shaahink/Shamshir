using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Tests.Integration.InfrastructureTests;

public sealed class UnifiedDecisionJournalTests
{
    [Fact]
    public async Task DecisionRecord_AcceptedAndRejected_BothPersistedInPipelineEvents()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"decjournal_test_{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            using var db = new TradingDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var repo = new SqlitePipelineEventRepository(db);
            var runId = $"test-run-{Guid.NewGuid():N}";

            var accepted = new PipelineEvent(
                Guid.NewGuid(), runId, 1, "OrderSubmitted", "EURUSD",
                DateTime.UtcNow, DateTime.UtcNow, "{}",
                null, null, null, "Order accepted: lots=0.1000 risk=28.00");

            var rejected = new PipelineEvent(
                Guid.NewGuid(), runId, 2, "OrderRejected", "EURUSD",
                DateTime.UtcNow, DateTime.UtcNow, "{}",
                null, null, "MAX_DAILY_LOSS", "Risk validation failed");

            await repo.AppendBatchAsync([accepted, rejected], CancellationToken.None);

            var retrieved = await repo.GetByRunIdAsync(runId, CancellationToken.None);

            retrieved.Should().HaveCount(2);

            var acceptedEvt = retrieved.Single(e => e.Stage == "OrderSubmitted");
            acceptedEvt.GuardResult.Should().BeNull();
            acceptedEvt.Reason.Should().Contain("Order accepted");

            var rejectedEvt = retrieved.Single(e => e.Stage == "OrderRejected");
            rejectedEvt.GuardResult.Should().Be("MAX_DAILY_LOSS");
            rejectedEvt.Reason.Should().Contain("Risk validation failed");
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (var i = 0; i < 10 && File.Exists(dbPath); i++)
            {
                try { File.Delete(dbPath); break; }
                catch (IOException) { Thread.Sleep(200); }
            }
        }
    }

    [Fact]
    public async Task DecisionRecord_GovernorStateChange_Persisted()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"decjournal_gov_{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            using var db = new TradingDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var repo = new SqlitePipelineEventRepository(db);
            var runId = $"test-run-{Guid.NewGuid():N}";

            var govEvent = new PipelineEvent(
                Guid.NewGuid(), runId, 1, "GovernorStateChanged", null,
                DateTime.UtcNow, DateTime.UtcNow, "{}",
                "Normal", "Reduced", null, "Reduced: daily DD 2.5% >= 2% band");

            await repo.AppendBatchAsync([govEvent], CancellationToken.None);

            var retrieved = await repo.GetByRunIdAsync(runId, CancellationToken.None);

            retrieved.Should().HaveCount(1);
            retrieved[0].Stage.Should().Be("GovernorStateChanged");
            retrieved[0].PhaseBefore.Should().Be("Normal");
            retrieved[0].PhaseAfter.Should().Be("Reduced");
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (var i = 0; i < 10 && File.Exists(dbPath); i++)
            {
                try { File.Delete(dbPath); break; }
                catch (IOException) { Thread.Sleep(200); }
            }
        }
    }

    [Fact]
    public async Task DecisionRecord_GuardResult_Persisted()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"decjournal_guard_{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            using var db = new TradingDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var repo = new SqlitePipelineEventRepository(db);
            var runId = $"test-run-{Guid.NewGuid():N}";

            var budgetBlocked = new PipelineEvent(
                Guid.NewGuid(), runId, 3, "OrderRejected", "EURUSD",
                DateTime.UtcNow, DateTime.UtcNow, "{}",
                null, null, "BudgetBlocked", "Budget exceeded: lots=0.5000 risk=140.00");

            await repo.AppendBatchAsync([budgetBlocked], CancellationToken.None);

            var retrieved = await repo.GetByRunIdAsync(runId, CancellationToken.None);

            retrieved.Should().HaveCount(1);
            var evt = retrieved[0];
            evt.Stage.Should().Be("OrderRejected");
            evt.GuardResult.Should().Be("BudgetBlocked");
            evt.Reason.Should().Contain("Budget exceeded");
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (var i = 0; i < 10 && File.Exists(dbPath); i++)
            {
                try { File.Delete(dbPath); break; }
                catch (IOException) { Thread.Sleep(200); }
            }
        }
    }
}
