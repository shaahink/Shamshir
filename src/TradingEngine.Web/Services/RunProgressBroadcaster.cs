using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using TradingEngine.Web.Hubs;

namespace TradingEngine.Web.Services;

/// <summary>
/// Pushes <see cref="RunProgress"/> envelopes to the per-run SignalR group (iter-21 U1). Throttles
/// to ≈4/sec per run so a fast M1 backtest can't flood the browser (the old per-bar SSE counter
/// did). A <c>force</c> publish bypasses the throttle for terminal/important frames (completed,
/// failed, breaches) so the final state is never swallowed by the throttle window.
/// </summary>
public sealed class RunProgressBroadcaster
{
    public static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(250);

    private readonly IHubContext<RunHub> _hub;
    private readonly ILogger<RunProgressBroadcaster> _logger;
    private readonly ConcurrentDictionary<string, long> _lastSentTicks = new();

    public RunProgressBroadcaster(IHubContext<RunHub> hub, ILogger<RunProgressBroadcaster> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    /// <summary>Send a progress frame to the run's group, subject to the per-run throttle unless
    /// <paramref name="force"/> is set. Returns true if it was actually sent.</summary>
    public bool Publish(RunProgress progress, bool force = false)
    {
        if (!force && !ShouldSend(progress.RunId))
            return false;

        _lastSentTicks[progress.RunId] = DateTime.UtcNow.Ticks;
        Send("RunProgress", progress);
        return true;
    }

    /// <summary>Terminal frame — always sent, and clears the run's throttle bookkeeping.</summary>
    public void PublishDone(RunProgress progress)
    {
        _lastSentTicks.TryRemove(progress.RunId, out _);
        Send("RunCompleted", progress);
    }

    public void RemoveRun(string runId)
    {
        _lastSentTicks.TryRemove(runId, out _);
    }

    // Fire-and-forget, but observe the task so a send/serialization failure is logged rather than
    // becoming a silent unobserved exception.
    private void Send(string method, RunProgress progress)
    {
        _ = _hub.Clients.Group(RunHub.Group(progress.RunId)).SendAsync(method, progress)
            .ContinueWith(t => _logger.LogWarning(t.Exception, "SignalR {Method} failed for run {RunId}", method, progress.RunId),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    private bool ShouldSend(string runId)
    {
        var now = DateTime.UtcNow.Ticks;
        if (!_lastSentTicks.TryGetValue(runId, out var last))
            return true;
        return new TimeSpan(now - last) >= ThrottleInterval;
    }
}
