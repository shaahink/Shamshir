using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class PersistentEquitySink : IEquitySink
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PersistentEquitySink> _logger;

    public PersistentEquitySink(IServiceScopeFactory scopeFactory, ILogger<PersistentEquitySink> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Observe(AccountSnapshot snapshot)
    {
        _ = PersistAsync(snapshot);
    }

    private async Task PersistAsync(AccountSnapshot snapshot)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IEquityRepository>();
            var equitySnapshot = new EquitySnapshot(
                snapshot.SimTimeUtc,
                snapshot.Balance,
                snapshot.FloatingPnL,
                snapshot.Equity,
                snapshot.PeakEquity,
                snapshot.DailyStartEquity,
                snapshot.DailyDrawdown,
                snapshot.MaxDrawdown,
                EngineMode.Live);
            await repo.SaveAsync(equitySnapshot, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PersistentEquitySink: failed to persist equity snapshot");
        }
    }
}
