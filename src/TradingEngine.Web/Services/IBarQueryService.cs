using TradingEngine.Web.Dtos.Bars;

namespace TradingEngine.Web.Services;

public interface IBarQueryService
{
    Task<IReadOnlyList<BarResponse>> GetBarsAsync(string symbol, string timeframe, DateTime? from, DateTime? to, CancellationToken ct);
}
