using TradingEngine.CTraderRunner;

namespace TradingEngine.Web.Services;

public interface IBacktestCommandService
{
    Task<string> StartAsync(BacktestConfig cfg, CancellationToken ct);
    void Cancel(string runId);
}
