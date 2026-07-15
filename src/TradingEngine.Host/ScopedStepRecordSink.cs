using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;

namespace TradingEngine.Host;

/// <summary>
/// Bridges the long-running, singleton <c>ChannelJournalWriter</c> flush loop to the scoped
/// <see cref="SqliteStepRecordSink"/> (iter-36 K5): each batch flush opens a fresh DI scope → a fresh
/// <see cref="TradingDbContext"/> → persists, then disposes the scope. This is the same scope-per-flush
/// pattern <c>PipelineEventWriter</c> used, so a per-run engine can journal losslessly to SQLite without
/// holding a DbContext open for the whole run.
/// </summary>
public sealed class ScopedStepRecordSink(IServiceScopeFactory scopeFactory) : IStepRecordSink
{
    public async Task AppendBatchAsync(IReadOnlyList<StepRecord> batch, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var cache = (IRunDataCache?)scope.ServiceProvider.GetService(typeof(IRunDataCache));
        await new SqliteStepRecordSink(db, cache).AppendBatchAsync(batch, ct);
    }
}
