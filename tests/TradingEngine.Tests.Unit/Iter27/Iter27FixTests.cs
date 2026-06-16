using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Events;

namespace TradingEngine.Tests.Unit.Iter27;

/// <summary>
/// Regression tests for the iter-27 UI/data fixes (see docs/iterations/iter-27/PLAN.md).
/// </summary>
[Trait("Category", "Infrastructure")]
[Trait("Speed", "Fast")]
public sealed class Iter27FixTests
{
    // ---- F-RunId: PipelineEventWriter stamps its own run id when a record carries none ----

    private sealed class CapturingPipelineRepo : IPipelineEventRepository
    {
        public List<PipelineEvent> Captured { get; } = [];
        public Task AppendBatchAsync(IReadOnlyList<PipelineEvent> events, CancellationToken ct)
        {
            Captured.AddRange(events);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PipelineEvent>> GetByRunIdAsync(string runId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<PipelineEvent>>(Captured.Where(e => e.RunId == runId).ToList());
    }

    [Fact]
    public async Task Record_stamps_writer_runId_when_record_runId_is_empty()
    {
        var repo = new CapturingPipelineRepo();
        var services = new ServiceCollection();
        services.AddScoped<IPipelineEventRepository>(_ => repo);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var writer = new PipelineEventWriter("run-42", scopeFactory, NullLogger<PipelineEventWriter>.Instance);

        // Lifecycle records hardcode RunId="" (the bug); a dispatcher record carries its own id.
        writer.Record(new DecisionRecord("", DateTime.UtcNow, 0, "EURUSD", "trend-breakout",
            null, "OrderFilled", null, null, "Filled", "{}"));
        writer.Record(new DecisionRecord("explicit-run", DateTime.UtcNow, 0, "EURUSD", "trend-breakout",
            null, "OrderSubmitted", null, null, "Accepted", "{}"));

        // DisposeAsync drains the channel synchronously via FlushRemainingAsync.
        await writer.DisposeAsync();

        repo.Captured.Should().HaveCount(2);
        repo.Captured.Should().ContainSingle(e => e.Stage == "OrderFilled")
            .Which.RunId.Should().Be("run-42", "an empty record RunId must be stamped with the writer's run id");
        repo.Captured.Should().ContainSingle(e => e.Stage == "OrderSubmitted")
            .Which.RunId.Should().Be("explicit-run", "a record that carries a RunId must keep it");
    }
}
