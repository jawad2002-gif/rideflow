using Microsoft.AspNetCore.SignalR;

namespace RideFlow.Realtime;

public class RideHub : Hub
{
    // For now anyone can join the "admin" group. JWT/roles come later.
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "admin");
        await base.OnConnectedAsync();
    }
}