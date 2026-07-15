using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.Experiments;

// F59: POST /api/experiments completion (and MarkFailed on the failure path) called UpdateAsync
// with a brand-new ExperimentEntity carrying the same Id as the one CreateAsync had already Added
// to this same DbContext. Because ExperimentRunner/SqliteExperimentRepository are request-scoped,
// both calls share one change tracker, so EF threw "another instance with the same key value is
// already being tracked" — leaving the row stuck at Status="Running" forever (MarkFailed hit the
// identical bug trying to record the failure). These tests reproduce that same-context sequence.
[Trait("Category", "Infrastructure")]
public sealed class ExperimentRepositoryTests : IDisposable
{
    private readonly SqliteInMemory _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task UpdateAsync_AfterCreateAsync_SameContext_DoesNotThrow()
    {
        using var ctx = _db.NewContext();
        var repo = new SqliteExperimentRepository(ctx);
        var id = Guid.NewGuid();
        var createdUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await repo.CreateAsync(new ExperimentEntity
        {
            Id = id,
            Name = "zero-variant-container",
            Hypothesis = "mint a container row",
            SpecJson = "{}",
            Status = "Running",
            CreatedUtc = createdUtc,
        }, CancellationToken.None);

        var act = async () => await repo.UpdateAsync(new ExperimentEntity
        {
            Id = id,
            Name = "zero-variant-container",
            Hypothesis = "mint a container row",
            SpecJson = "{}",
            Status = "Completed",
            CreatedUtc = DateTime.UtcNow, // caller always recomputes this — must not clobber
            CompletedUtc = DateTime.UtcNow,
        }, CancellationToken.None);

        await act.Should().NotThrowAsync();

        var stored = await repo.GetByIdAsync(id, CancellationToken.None);
        stored.Should().NotBeNull();
        stored!.Status.Should().Be("Completed");
        stored.CreatedUtc.Should().Be(createdUtc, "an update must never overwrite the original creation time");
        stored.CompletedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkFailed_AfterCreateAsync_SameContext_PersistsFailureInsteadOfLeakingRunningRow()
    {
        using var ctx = _db.NewContext();
        var repo = new SqliteExperimentRepository(ctx);
        var id = Guid.NewGuid();

        await repo.CreateAsync(new ExperimentEntity
        {
            Id = id,
            Name = "zero-variant-experiment",
            Hypothesis = "",
            SpecJson = "{\"variants\":[]}",
            Status = "Running",
            CreatedUtc = DateTime.UtcNow,
        }, CancellationToken.None);

        // Mirrors ExperimentRunner.MarkFailed: reconstructs a fresh entity with the same Id.
        await repo.UpdateAsync(new ExperimentEntity
        {
            Id = id,
            Name = "zero-variant-experiment",
            Hypothesis = "",
            SpecJson = "{\"variants\":[]}",
            Status = "Failed: boom",
            CreatedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        }, CancellationToken.None);

        var stored = await repo.GetByIdAsync(id, CancellationToken.None);
        stored!.Status.Should().Be("Failed: boom", "the failure must be recorded, not left as an orphaned 'Running' row");
    }
}
