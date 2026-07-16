using Microsoft.Extensions.Caching.Memory;
using TradingEngine.Web.Dtos.Runs;

namespace TradingEngine.Web.Services;

/// <summary>Composition facade for the run read side (<see cref="IRunQueryService"/>). The four
/// query paths live in Runs/ — the list + healing overlays (<see cref="RunListQuery"/>), single-run
/// detail (<see cref="RunDetailQuery"/>), cache-first run data (<see cref="RunDataQuery"/>) and the
/// journal bar narratives (<see cref="RunBarNarrativeQuery"/>). Live in-flight runs are observed
/// through the narrow <see cref="ILiveRunReader"/> port, not the orchestrator class.</summary>
public sealed class RunQueryService : IRunQueryService
{
    private readonly RunListQuery _list;
    private readonly RunDetailQuery _detail;
    private readonly RunDataQuery _data;
    private readonly RunBarNarrativeQuery _bars;

    public RunQueryService(
        TradingDbContext db,
        IBacktestRunRepository runRepo,
        IEquityRepository equityRepo,
        IJournalQueryRepository journals,
        IMemoryCache? memoryCache = null,
        IRunDataCache? runDataCache = null,
        ILiveRunReader? liveRuns = null)
    {
        _list = new RunListQuery(db, memoryCache, liveRuns);
        _detail = new RunDetailQuery(runRepo, runDataCache, liveRuns);
        _data = new RunDataQuery(db, equityRepo, runDataCache);
        _bars = new RunBarNarrativeQuery(journals, runDataCache);
    }

    public Task<IReadOnlyList<RunListResponse>> GetRunsAsync(CancellationToken ct) =>
        _list.GetRunsAsync(ct);

    public void InvalidateRunsCache() => _list.InvalidateRunsCache();

    public Task<RunDetailResponse?> GetRunAsync(string runId, CancellationToken ct) =>
        _detail.GetRunAsync(runId, ct);

    public Task<IReadOnlyList<TradeSummaryResponse>> GetRunTradesAsync(string runId, CancellationToken ct) =>
        _data.GetRunTradesAsync(runId, ct);

    public Task<IReadOnlyList<EquityPointResponse>> GetRunEquityAsync(string runId, CancellationToken ct) =>
        _data.GetRunEquityAsync(runId, ct);

    public Task<IReadOnlyList<DailyPnlResponse>> GetRunDailyPnLAsync(string runId, CancellationToken ct) =>
        _data.GetRunDailyPnLAsync(runId, ct);

    public Task<RunAnalyticsResponse?> GetRunAnalyticsAsync(string runId, CancellationToken ct) =>
        _data.GetRunAnalyticsAsync(runId, ct);

    public Task<IReadOnlyList<BarNarrativeResponse>> GetRunBarsAsync(string runId, DateTime? from, DateTime? to, CancellationToken ct) =>
        _bars.GetRunBarsAsync(runId, from, to, ct);
}
