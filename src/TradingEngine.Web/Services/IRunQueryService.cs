using TradingEngine.Web.Dtos.Runs;

namespace TradingEngine.Web.Services;

public interface IRunQueryService
{
    Task<IReadOnlyList<RunListResponse>> GetRunsAsync(CancellationToken ct);
    Task<RunDetailResponse?> GetRunAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<TradeSummaryResponse>> GetRunTradesAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<EquityPointResponse>> GetRunEquityAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<DailyPnlResponse>> GetRunDailyPnLAsync(string runId, CancellationToken ct);
    Task<RunAnalyticsResponse?> GetRunAnalyticsAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<BarNarrativeResponse>> GetRunBarsAsync(string runId, DateTime? from, DateTime? to, CancellationToken ct);
    void InvalidateRunsCache();
}
