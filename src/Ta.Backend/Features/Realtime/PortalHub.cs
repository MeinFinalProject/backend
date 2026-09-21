using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Ta.Backend.Features.Realtime;

// No client-invokable mutations or arbitrary subscriptions. All product reads use authorized HTTP APIs.
public sealed class PortalHub(PortalConnections connections) : Hub
{
    public override Task OnConnectedAsync()
    {
        connections.Items[Context.ConnectionId] = new PortalConnection(
            Guid.Parse(Context.User!.FindFirstValue("session_id")!),
            Context.User!.FindFirstValue(ClaimTypes.Role)!, Context.Abort);
        return base.OnConnectedAsync();
    }
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Items.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }
}

public sealed record PortalConnection(Guid SessionId, string Role, Action Abort);
public sealed class PortalConnections
{
    public ConcurrentDictionary<string, PortalConnection> Items { get; } = new();
}
