using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Ta.Backend.Features.Identity;
using Ta.Backend.Features.Devices;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Realtime;

// Coalesced, best-effort invalidations. Database commits never depend on socket delivery.
// No names, student IDs, event payloads, photos or embeddings enter this channel.
public sealed class RealtimeUpdates(IServiceScopeFactory scopes, PortalConnections portal,
    DeviceConnections devices, IHubContext<PortalHub> hub, ILogger<RealtimeUpdates> logger) : BackgroundService
{
    private int academicPending;
    private int devicesPending;
    public void AcademicCommitted()
    {
        Interlocked.Exchange(ref academicPending, 1);
        DevicesChanged();
    }
    public void GalleryCommitted() { devices.Reconcile(); DevicesChanged(); }
    public void DevicesChanged() => Interlocked.Exchange(ref devicesPending, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var ct = timeout.Token;
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BackendDbContext>();
                await devices.Validate(db, ct);
                var clients = portal.Items.ToArray();
                var ids = clients.Select(c => c.Value.SessionId).ToArray();
                var valid = await (from s in db.Set<AccountSession>() join a in db.Set<Account>() on s.AccountId equals a.AccountId
                    where ids.Contains(s.AccountSessionId) && !s.AccountSessionRevoked && s.AccountSessionExpiresAt > DateTimeOffset.UtcNow
                        && a.AccountStatus == "approved"
                    select new { s.AccountSessionId, a.AccountRole }).ToDictionaryAsync(s => s.AccountSessionId, s => s.AccountRole, ct);
                foreach (var client in clients)
                    if (!valid.TryGetValue(client.Value.SessionId, out var role) || role != client.Value.Role)
                    {
                        client.Value.Abort();
                        portal.Items.TryRemove(client.Key, out _);
                    }
                var academic = Interlocked.Exchange(ref academicPending, 0) != 0;
                var device = Interlocked.Exchange(ref devicesPending, 0) != 0;
                foreach (var client in clients.Where(c => valid.GetValueOrDefault(c.Value.SessionId) == c.Value.Role))
                {
                    if (academic) await hub.Clients.Client(client.Key).SendAsync("invalidate", new { topic = "academic" }, ct);
                    if (device && client.Value.Role == Roles.Administrator)
                        await hub.Clients.Client(client.Key).SendAsync("invalidate", new { topic = "devices" }, ct);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                // Fail closed if authorization cannot be revalidated. Clients reconcile after reconnect.
                foreach (var client in portal.Items.Values) client.Abort();
                devices.AbortAll();
                logger.LogWarning("Real-time reconciliation unavailable ({ErrorType})", e.GetType().Name);
            }
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        devices.AbortAll();
        foreach (var client in portal.Items.Values) client.Abort();
        await base.StopAsync(cancellationToken);
    }
}
