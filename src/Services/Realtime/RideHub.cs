using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace RideFlow.Realtime;

[Authorize]
public class RideHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst("sub")?.Value;
        var role = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;

        if (!string.IsNullOrEmpty(userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");

        if (role == "Admin")
            await Groups.AddToGroupAsync(Context.ConnectionId, "admin");
        if (role == "Driver")
            await Groups.AddToGroupAsync(Context.ConnectionId, "drivers");
        if (role is "Rider" or "Admin")
            await Groups.AddToGroupAsync(Context.ConnectionId, "map"); // live car positions

        await base.OnConnectedAsync();
    }
}
