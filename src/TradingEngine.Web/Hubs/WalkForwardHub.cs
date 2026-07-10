using Microsoft.AspNetCore.SignalR;

namespace TradingEngine.Web.Hubs;

public sealed class WalkForwardHub : Hub
{
    public async Task JoinJob(string jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"wf:{jobId}");
    }

    public Task LeaveJob(string jobId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, $"wf:{jobId}");
    }
}
