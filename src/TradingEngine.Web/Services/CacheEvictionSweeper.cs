using Microsoft.Extensions.Logging;

namespace TradingEngine.Web.Services;

public sealed class CacheEvictionSweeper : IHostedService, IDisposable
{
    private readonly IRunDataCache? _cache;
    private readonly ILogger<CacheEvictionSweeper> _logger;
    private Timer? _timer;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(60);
    private const int MaxResidentRuns = 8;

    public CacheEvictionSweeper(IRunDataCache? cache, ILogger<CacheEvictionSweeper> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_cache is null) return Task.CompletedTask;
        _timer = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private void Sweep()
    {
        if (_cache is null) return;

        try
        {
            var now = DateTime.UtcNow;
            var runIds = _cache.GetRunIds();
            var completedOverGrace = new List<string>();

            foreach (var id in runIds)
            {
                var completedAt = _cache.GetCompletedAtUtc(id);
                if (completedAt.HasValue && (now - completedAt.Value) > GracePeriod)
                    completedOverGrace.Add(id);
            }

            foreach (var id in completedOverGrace)
            {
                _cache.Evict(id);
                _logger.LogDebug("CacheEvictionSweeper: evicted {RunId} (grace expired)", id);
            }

            if (completedOverGrace.Count > 0)
                _logger.LogInformation("CacheEvictionSweeper: evicted {Count} runs past grace", completedOverGrace.Count);

            runIds = _cache.GetRunIds();
            if (runIds.Count > MaxResidentRuns)
            {
                var excess = runIds.Count - MaxResidentRuns;
                var completions = new List<(string Id, DateTime? CompletedAt)>();
                foreach (var id in runIds)
                    completions.Add((id, _cache.GetCompletedAtUtc(id)));

                var toEvict = completions
                    .Where(c => c.CompletedAt.HasValue)
                    .OrderBy(c => c.CompletedAt!.Value)
                    .Take(excess)
                    .Select(c => c.Id)
                    .ToList();

                foreach (var id in toEvict)
                {
                    _cache.Evict(id);
                    _logger.LogInformation("CacheEvictionSweeper: evicted {RunId} (cap enforcement)", id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CacheEvictionSweeper sweep failed");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
