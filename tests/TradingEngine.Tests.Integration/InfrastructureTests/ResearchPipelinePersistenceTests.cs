using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace TradingEngine.Tests.Integration.InfrastructureTests;

/// <summary>
/// P3.2 (Q6) — the research-pipeline state tables (ResearchPipelines + ResearchPipelineSteps) persist a
/// resumable, owner-reviewable pipeline. These pin the shape the CLI executor relies on: steps cascade
/// off the pipeline, (PipelineId, StepIndex) is unique, and a step's recorded verdict survives a reload
/// (the resume source of truth). Runs on real SQLite (:memory:) per D10.
/// </summary>
public sealed class ResearchPipelinePersistenceTests
{
    private static TradingDbContext NewDb(SqliteConnection conn)
    {
        var opts = new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlite(conn)
            .AddInterceptors(new AuditStampInterceptor(() => DateTime.UtcNow))
            .Options;
        var db = new TradingDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task Pipeline_WithSteps_RoundTrips_AndVerdictSurvivesReload()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var id = Guid.NewGuid();
        using (var db = NewDb(conn))
        {
            db.ResearchPipelines.Add(new ResearchPipelineEntity
            {
                Id = id,
                Name = "venue-parity",
                PlaybookJson = """{"name":"venue-parity","steps":[]}""",
                Status = "running",
                StartedAtUtc = DateTime.UtcNow,
                Steps =
                [
                    new ResearchPipelineStepEntity { Id = Guid.NewGuid(), PipelineId = id, StepIndex = 0, Kind = "ensure-data", Status = "pending", ParamHash = "aaa" },
                    new ResearchPipelineStepEntity { Id = Guid.NewGuid(), PipelineId = id, StepIndex = 1, Kind = "start-run", Status = "pending", ParamHash = "bbb" },
                ],
            });
            await db.SaveChangesAsync();
        }

        using (var db = NewDb(conn))
        {
            var step = await db.ResearchPipelineSteps.FirstAsync(s => s.PipelineId == id && s.StepIndex == 0);
            step.Status = "passed";
            step.VerdictJson = """{"pass":true,"cells":2}""";
            await db.SaveChangesAsync();
        }

        using (var db = NewDb(conn))
        {
            var p = await db.ResearchPipelines.Include(x => x.Steps).FirstAsync(x => x.Id == id);
            p.Name.Should().Be("venue-parity");
            p.Steps.Should().HaveCount(2);
            var s0 = p.Steps.Single(s => s.StepIndex == 0);
            s0.Status.Should().Be("passed");
            s0.VerdictJson.Should().Contain("cells");
            p.Steps.Single(s => s.StepIndex == 1).Status.Should().Be("pending");
        }
    }

    [Fact]
    public async Task Steps_AreUnique_PerPipelineIndex()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var id = Guid.NewGuid();
        using var db = NewDb(conn);
        db.ResearchPipelines.Add(new ResearchPipelineEntity
        {
            Id = id, Name = "dup", PlaybookJson = "{}", Status = "running", StartedAtUtc = DateTime.UtcNow,
            Steps = [new ResearchPipelineStepEntity { Id = Guid.NewGuid(), PipelineId = id, StepIndex = 0, Kind = "report", Status = "pending" }],
        });
        await db.SaveChangesAsync();

        db.ResearchPipelineSteps.Add(new ResearchPipelineStepEntity
        {
            Id = Guid.NewGuid(), PipelineId = id, StepIndex = 0, Kind = "report", Status = "pending",
        });
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("(PipelineId, StepIndex) is a unique index");
    }

    [Fact]
    public async Task DeletingPipeline_CascadesSteps()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();

        var id = Guid.NewGuid();
        using var db = NewDb(conn);
        db.ResearchPipelines.Add(new ResearchPipelineEntity
        {
            Id = id, Name = "cascade", PlaybookJson = "{}", Status = "running", StartedAtUtc = DateTime.UtcNow,
            Steps = [new ResearchPipelineStepEntity { Id = Guid.NewGuid(), PipelineId = id, StepIndex = 0, Kind = "report", Status = "pending" }],
        });
        await db.SaveChangesAsync();

        db.ResearchPipelines.Remove(await db.ResearchPipelines.FirstAsync(p => p.Id == id));
        await db.SaveChangesAsync();

        (await db.ResearchPipelineSteps.CountAsync(s => s.PipelineId == id)).Should().Be(0);
    }
}
