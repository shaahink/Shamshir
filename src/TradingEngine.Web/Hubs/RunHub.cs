using Microsoft.AspNetCore.SignalR;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Hubs;

/// <summary>
/// Live run channel (iter-21 U1). Clients join a per-run group keyed by <c>runId</c>; the engine
/// publishes a throttled <see cref="Services.RunProgress"/> envelope to that group via
/// <see cref="Services.RunProgressBroadcaster"/>. One-shot reads still go over AJAX — this hub
/// carries only the live stream (progress / journal / done).
/// </summary>
public sealed class RunHub : Hub
{
    private readonly BacktestOrchestrator _orchestrator;

    public RunHub(BacktestOrchestrator orchestrator) => _orchestrator = orchestrator;

    public static string Group(string runId) => $"run:{runId}";

    public async Task JoinRun(string runId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(runId));

        // iter-redesign P6.1: snapshot-on-join — a page load / reconnect mid-run gets the CURRENT
        // progress immediately (direct to the caller, bypassing the throttle) instead of a blank monitor
        // until the next broadcast. Fixes the "live monitor stopped working / can't self-verify" symptom.
        var progress = _orchestrator.GetCurrentProgress(runId);
        if (progress is not null)
        {
            var method = progress.Status == "running" ? "RunProgress" : "RunCompleted";
            await Clients.Caller.SendAsync(method, progress);
        }
    }

    public Task LeaveRun(string runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(runId));
}
