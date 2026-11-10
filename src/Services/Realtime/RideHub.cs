using Microsoft.AspNetCore.SignalR;

namespace RideFlow.Realtime;

public class RideHub : Hub
{
    // Temporary: no auth yet, so the client says who it is. JWT replaces this later.
    public override async Task OnConnectedAsync()
    {
        var query = Context.GetHttpContext()?.Request.Query;
        var driverId = query?["driverId"].ToString();

        if (driverId == "all")
            await Groups.AddToGroupAsync(Context.ConnectionId, "debug-offers");
        else if (!string.IsNullOrEmpty(driverId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{driverId}");
        else
            await Groups.AddToGroupAsync(Context.ConnectionId, "admin");

        await base.OnConnectedAsync();
    }
}