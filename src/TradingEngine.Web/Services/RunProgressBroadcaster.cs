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
    private readonly ConcurrentDictionary<string, long> _lastSentTicks = new();

    public RunProgressBroadcaster(IHubContext<RunHub> hub) => _hub = hub;

    /// <summary>Send a progress frame to the run's group, subject to the per-run throttle unless
    /// <paramref name="force"/> is set. Returns true if it was actually sent.</summary>
    public bool Publish(RunProgress progress, bool force = false)
    {
        if (!force && !ShouldSend(progress.RunId))
            return false;

        _lastSentTicks[progress.RunId] = DateTime.UtcNow.Ticks;
        _ = _hub.Clients.Group(RunHub.Group(progress.RunId)).SendAsync("onProgress", progress);
        return true;
    }

    /// <summary>Terminal frame — always sent, and clears the run's throttle bookkeeping.</summary>
    public void PublishDone(RunProgress progress)
    {
        _lastSentTicks.TryRemove(progress.RunId, out _);
        _ = _hub.Clients.Group(RunHub.Group(progress.RunId)).SendAsync("onDone", progress);
    }

    private bool ShouldSend(string runId)
    {
        var now = DateTime.UtcNow.Ticks;
        if (!_lastSentTicks.TryGetValue(runId, out var last))
            return true;
        return new TimeSpan(now - last) >= ThrottleInterval;
    }
}
