using Microsoft.AspNetCore.SignalR;

namespace TradingEngine.Web.Hubs;

/// <summary>
/// Live run channel (iter-21 U1). Clients join a per-run group keyed by <c>runId</c>; the engine
/// publishes a throttled <see cref="Services.RunProgress"/> envelope to that group via
/// <see cref="Services.RunProgressBroadcaster"/>. One-shot reads still go over AJAX — this hub
/// carries only the live stream (progress / journal / done).
/// </summary>
public sealed class RunHub : Hub
{
    public static string Group(string runId) => $"run:{runId}";

    public Task JoinRun(string runId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, Group(runId));

    public Task LeaveRun(string runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(runId));
}
