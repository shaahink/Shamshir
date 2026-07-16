using Microsoft.Extensions.Caching.Memory;
using TradingEngine.Web.Dtos.Runs;

namespace TradingEngine.Web.Services;

/// <summary>The runs-list read path: the DB projection plus the healing/enrichment overlays
/// (persisted-terminal-status preference, stale trade counts, stuck-run detection, live-run
/// status overlay, strategy derivation, latest scores) and the 2-second list cache.
/// Extracted verbatim from RunQueryService.</summary>
public sealed class RunListQuery(
    TradingDbContext db,
    IMemoryCache? memoryCache,
    ILiveRunReader? liveRuns)
{
    private readonly TradingDbContext _db = db;
    private readonly IMemoryCache? _memoryCache = memoryCache;
    private readonly ILiveRunReader? _live = liveRuns;

    private static readonly TimeSpan RunsListCacheDuration = TimeSpan.FromSeconds(2);
    private const string RunsListCacheKey = "runs:all";

    public async Task<IReadOnlyList<RunListResponse>> GetRunsAsync(CancellationToken ct)
    {
        if (_memoryCache is not null && _memoryCache.TryGetValue(RunsListCacheKey, out IReadOnlyList<RunListResponse>? cached) && cached is not null)
        {
            return cached;
        }

        var runs = await _db.BacktestRuns
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(50)
            .Select(r => new RunListResponse
            {
                RunId = r.RunId,
                CreatedAtUtc = r.CreatedAtUtc,
                Status = RunStatusResolver.Resolve(
                    isCompleted: r.CompletedAtUtc != default,
                    errorMessage: r.ErrorMessage,
                    warningsJson: r.WarningsJson),
                Symbol = r.Symbol,
                Period = r.Period,
                Symbols = r.Symbols,
                Periods = r.Periods,
                StartedAtUtc = r.StartedAtUtc,
                CompletedAtUtc = r.CompletedAtUtc == default ? null : r.CompletedAtUtc,
                NetProfit = r.NetProfit,
                GrossPnL = r.GrossPnL,
                CommissionTotal = r.CommissionTotal,
                SwapTotal = r.SwapTotal,
                MaxDrawdownPct = r.MaxDrawdownPct,
                TotalTrades = r.TotalTrades,
                WinningTrades = r.WinningTrades,
                WinRatePct = r.WinRatePct,
                ErrorMessage = r.ErrorMessage,
                Venue = r.Venue ?? "replay",
                RiskProfileId = r.RiskProfileId,
                WarningsJson = r.WarningsJson,
                ParentRunId = r.ParentRunId,
                ComparePairId = r.ComparePairId,
                QueuePosition = r.QueuePosition,
                PersistedStatus = r.Status,
                WallElapsedMs = r.WallElapsedMs,
                Notes = r.Notes,
                RunPlanJson = r.RunPlanJson,
            })
            .ToListAsync(ct);

        PreferPersistedTerminalStatus(runs);
        await FixStaleTradeCounts(runs, ct);
        FixStuckRunStatuses(runs);
        OverlayLiveRunStatuses(runs);
        DeriveStrategies(runs);
        await AttachLatestScores(runs, ct);

        _memoryCache?.Set(RunsListCacheKey, runs, RunsListCacheDuration);
        return runs;
    }

    public void InvalidateRunsCache() => _memoryCache?.Remove(RunsListCacheKey);

    // X0: the DB projection above derives Status via the legacy ExitCode/ErrorMessage-only
    // RunStatusResolver.Resolve, which can never say "cancelled" — see RunStatusOverlay.ResolveStatus
    // for the same fix on the single-run path. The persisted Status column (written by
    // WriteEndRecordAsync) is authoritative when it names one of the four real terminal states.
    private static void PreferPersistedTerminalStatus(List<RunListResponse> runs)
    {
        for (var i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            if (r.PersistedStatus is not null && RunStateMachine.TerminalStates.Contains(r.PersistedStatus) && r.PersistedStatus != r.Status)
            {
                runs[i] = r with { Status = r.PersistedStatus };
            }
        }
    }

    private void OverlayLiveRunStatuses(List<RunListResponse> runs)
    {
        if (_live is null)
        {
            return;
        }
        var liveStates = _live.GetAll();
        if (liveStates.Count == 0)
        {
            return;
        }

        var liveMap = liveStates.ToDictionary(s => s.RunId);
        for (var i = 0; i < runs.Count; i++)
        {
            if (liveMap.TryGetValue(runs[i].RunId, out var state))
            {
                runs[i] = runs[i] with
                {
                    Status = state.Status,
                    QueuePosition = _live.GetQueuePosition(runs[i].RunId),
                };
            }
        }
    }

    // X2: distinct strategy ids from the persisted run plan, for the runs-table Strategy column.
    // RunPlanJson entries are PascalCase (persisted server-side), but tolerate camelCase too.
    private static void DeriveStrategies(List<RunListResponse> runs)
    {
        for (var i = 0; i < runs.Count; i++)
        {
            var raw = runs[i].RunPlanJson;
            if (string.IsNullOrEmpty(raw) || raw == "[]")
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                var ids = new List<string>();
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    if (entry.TryGetProperty("StrategyId", out var sid) || entry.TryGetProperty("strategyId", out sid))
                    {
                        var id = sid.GetString();
                        if (!string.IsNullOrEmpty(id) && !ids.Contains(id))
                        {
                            ids.Add(id);
                        }
                    }
                }
                if (ids.Count > 0)
                {
                    runs[i] = runs[i] with { Strategies = string.Join(", ", ids) };
                }
            }
            catch (JsonException) { /* malformed plan — leave Strategies null */ }
        }
    }

    // X2: latest SetupScore composite per run (ExperimentRuns.ScoreJson, PascalCase "Composite").
    private async Task AttachLatestScores(List<RunListResponse> runs, CancellationToken ct)
    {
        if (runs.Count == 0)
        {
            return;
        }
        var ids = runs.Select(r => r.RunId).ToHashSet();

        List<(string RunId, string ScoreJson)> scored;
        try
        {
            scored = (await _db.ExperimentRuns
                .AsNoTracking()
                .Where(er => ids.Contains(er.BacktestRunId))
                .OrderBy(er => er.UpdatedAtUtc)
                .Select(er => new { er.BacktestRunId, er.ScoreJson })
                .ToListAsync(ct))
                .Select(x => (x.BacktestRunId, x.ScoreJson))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return; // scores are decoration on this page — never fail the list for them
        }

        // OrderBy UpdatedAtUtc ASC + dictionary overwrite ⇒ the latest score wins per run.
        var latest = new Dictionary<string, double>();
        foreach (var (runId, scoreJson) in scored)
        {
            if (string.IsNullOrEmpty(scoreJson) || scoreJson == "{}")
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(scoreJson);
                if (doc.RootElement.TryGetProperty("Composite", out var comp) && comp.ValueKind == JsonValueKind.Number)
                {
                    latest[runId] = comp.GetDouble();
                }
            }
            catch (JsonException) { /* skip malformed */ }
        }

        if (latest.Count == 0)
        {
            return;
        }
        for (var i = 0; i < runs.Count; i++)
        {
            if (latest.TryGetValue(runs[i].RunId, out var score))
            {
                runs[i] = runs[i] with { Score = score };
            }
        }
    }

    private async Task FixStaleTradeCounts(List<RunListResponse> runs, CancellationToken ct)
    {
        var zeroTradeRunIds = runs
            .Where(r => r.TotalTrades == 0)
            .Select(r => r.RunId)
            .Take(200)
            .ToHashSet();

        if (zeroTradeRunIds.Count == 0)
        {
            return;
        }

        var actualCounts = await _db.Trades
            .AsNoTracking()
            .Where(t => t.RunId != null && zeroTradeRunIds.Contains(t.RunId))
            .GroupBy(t => t.RunId!)
            .Select(g => new { RunId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        if (actualCounts.Count == 0)
        {
            return;
        }

        var countMap = actualCounts.ToDictionary(x => x.RunId, x => x.Count);
        for (var i = 0; i < runs.Count; i++)
        {
            if (runs[i].TotalTrades == 0 && countMap.TryGetValue(runs[i].RunId, out var actual) && actual > 0)
            {
                runs[i] = runs[i] with { TotalTrades = actual };
            }
        }
    }

    private void FixStuckRunStatuses(List<RunListResponse> runs)
    {
        for (var i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            if (r.Status == "running" && r.CompletedAtUtc is null
                && DateTime.UtcNow - r.StartedAtUtc > RunStatusOverlay.StuckThreshold
                && (_live?.GetState(r.RunId) is null))
            {
                runs[i] = r with { Status = "failed", ErrorMessage = (r.ErrorMessage ?? "") + " Timed out (stuck)." };
            }

            if (r.PersistedStatus == "queued" && r.Status != "running"
                && DateTime.UtcNow - r.StartedAtUtc > RunStatusOverlay.StuckThreshold
                && (_live?.GetState(r.RunId) is null))
            {
                runs[i] = r with { Status = "cancelled", ErrorMessage = (r.ErrorMessage ?? "") + " Orphaned queued run." };
            }
        }
    }
}
