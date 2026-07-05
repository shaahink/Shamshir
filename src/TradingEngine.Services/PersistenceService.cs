using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Services;

public sealed class PersistenceService(
    IServiceScopeFactory scopeFactory,
    ILogger<PersistenceService> logger)
{
    public async Task SaveTradeAsync(TradeResult trade, string runId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
            await repo.SaveAsync(trade, runId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save trade. TradeId={TradeId}", trade.Id);
        }
    }

    public async Task SaveExcursionAsync(string runId, Guid positionId, string pathJson, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IExcursionRepository>();
            await repo.SaveAsync(runId, positionId, pathJson, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save trade excursion. RunId={RunId} PositionId={PositionId}", runId, positionId);
        }
    }

    public async Task SaveEquitySnapshotAsync(EquitySnapshot snapshot, string? runId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IEquityRepository>();
            await repo.SaveAsync(snapshot, runId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save equity snapshot");
        }
    }

    public async Task SaveEquitySnapshotsBatchAsync(IReadOnlyList<EquitySnapshot> snapshots, string? runId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IEquityRepository>();
            await repo.SaveBatchAsync(snapshots, runId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save {Count} equity snapshots", snapshots.Count);
        }
    }
}
