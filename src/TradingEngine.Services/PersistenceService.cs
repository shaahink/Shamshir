using Microsoft.Extensions.Logging;

namespace TradingEngine.Services;

public sealed class PersistenceService(
    ITradeRepository tradeRepository,
    IEquityRepository equityRepository,
    ILogger<PersistenceService> logger)
{
    public async Task SaveTradeAsync(TradeResult trade, CancellationToken ct)
    {
        try
        {
            await tradeRepository.SaveAsync(trade, ct);
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
            await equityRepository.SaveAsync(snapshot, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save equity snapshot");
        }
    }
}
