using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Services;

public sealed class PersistenceService(
    IServiceScopeFactory scopeFactory,
    ILogger<PersistenceService> logger)
{
    public async Task SaveTradeAsync(TradeResult trade, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<ITradeRepository>();
            await repo.SaveAsync(trade, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save trade. TradeId={TradeId}", trade.Id);
        }
    }

    public async Task SaveEquitySnapshotAsync(EquitySnapshot snapshot, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IEquityRepository>();
            await repo.SaveAsync(snapshot, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save equity snapshot");
        }
    }
}
