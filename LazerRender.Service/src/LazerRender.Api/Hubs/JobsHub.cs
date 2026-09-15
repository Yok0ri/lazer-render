using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LazerRender.Api.Hubs;

/// <summary>
/// Realtime progress hub. Clients subscribe to a job id and the worker broadcasts progress
/// to that group.
/// </summary>
[Authorize]
public sealed class JobsHub : Hub
{
    public async Task Subscribe(string jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, jobId);
    }

    public async Task Unsubscribe(string jobId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId);
    }
}
