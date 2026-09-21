using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.AcademicManagement;
using Ta.Backend.Features.Realtime;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Devices;

public static class DeviceRealtimeEndpoints
{
    public static void MapDeviceRealtimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/device-channel", Connect).RequireAuthorization(Credentials.DeviceScheme).ExcludeFromDescription();
        endpoints.MapGet("/admin/devices/operational-status", Status)
            .RequireAuthorization(Credentials.AdminScheme).WithTags("Devices")
            .Produces<List<DeviceOperationalView>>()
            .WithSummary("Read current device connectivity, installed gallery and operational readiness");
    }

    private static async Task<IResult> Status(BackendDbContext db, DeviceConnections connections, CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
        var states = await db.Set<DeviceOperationalStatus>().AsNoTracking().ToDictionaryAsync(s => s.DeviceId, ct);
        var now = DateTimeOffset.UtcNow;
        var assigned = await (from a in db.Set<DeviceRoomAssignment>() join r in db.Set<Classroom>() on a.ClassroomId equals r.ClassroomId
            where a.DeviceRoomAssignmentValidFrom <= now && (a.DeviceRoomAssignmentValidUntil == null || a.DeviceRoomAssignmentValidUntil > now)
                && r.ClassroomActive select a.DeviceId).ToListAsync(ct);
        var gallery = await db.GalleryReleases.AsNoTracking().OrderByDescending(g => g.GalleryReleaseId)
            .Select(g => new { g.GalleryVersion, g.GalleryModelSha256, g.GalleryTemplateCount }).FirstOrDefaultAsync(ct);
        return Results.Ok(devices.OrderBy(d => d.DeviceId).Select(d =>
        {
            var s = states.GetValueOrDefault(d.DeviceId);
            var connected = d.DeviceEnabled && connections.Connected(d.DeviceId);
            var fresh = s != null && s.DeviceLastSeenAt > now.AddSeconds(-45);
            var reasons = new List<string>();
            if (!d.DeviceEnabled) reasons.Add("disabled");
            if (!connected) reasons.Add("disconnected");
            if (!fresh) reasons.Add("telemetry_stale");
            if (s?.DeviceRuntimeState != "running") reasons.Add(s?.DeviceRuntimeState ?? "not_reported");
            // Frame age advances between reports; a frozen pipeline cannot remain healthy on a recent heartbeat alone.
            if (s?.DeviceFrameAgeMs == null || s.DeviceFrameAgeMs + (now - s.DeviceLastSeenAt).TotalMilliseconds > 20000)
                reasons.Add("camera_stale");
            if (s?.DeviceOutboxDeadCount > 0) reasons.Add("delivery_rejected");
            var healthy = reasons.Count == 0;
            if (!assigned.Contains(d.DeviceId)) reasons.Add("room_unassigned");
            if (gallery == null || s?.DeviceInstalledGalleryVersion != gallery.GalleryVersion
                || s.DeviceModelSha256 != gallery.GalleryModelSha256
                || s.DeviceInstalledTemplateCount != gallery.GalleryTemplateCount) reasons.Add("gallery_pending");
            if (s?.DeviceInstalledTemplateCount is null or 0) reasons.Add("gallery_empty");
            return new DeviceOperationalView(d.DeviceId, connected, healthy, reasons.Count == 0,
                reasons, gallery?.GalleryVersion, s);
        }));
    }

    private static async Task Connect(HttpContext context, DeviceConnections connections, IServiceScopeFactory scopes,
        RealtimeUpdates updates, IHostApplicationLifetime lifetime, ILoggerFactory loggers)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        // Native clients do not send Origin. Browser devices must not reuse the bearer channel.
        if (context.Request.Headers.ContainsKey("Origin")) { context.Response.StatusCode = 403; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new DeviceConnection(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!,
            Credentials.Hash(context.Request.Headers.Authorization.ToString()[7..]), socket);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        Task? sending = null;
        try
        {
            await connections.Gate.WaitAsync(stop.Token);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BackendDbContext>();
                if (!await Allowed(db, connection, stop.Token)) return;
                // A new connection must report its own state before readiness can become true.
                await db.Set<DeviceOperationalStatus>().Where(s => s.DeviceId == connection.DeviceId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeviceRuntimeState, "starting"), stop.Token);
                connections.Add(connection);
            }
            finally { connections.Gate.Release(); }
            updates.DevicesChanged();
            sending = Send(connection, stop.Token);
            var buffer = new byte[4096];
            var lastMessage = DateTimeOffset.MinValue;
            while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var used = 0;
                ValueWebSocketReceiveResult part;
                do
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(45));
                    part = await socket.ReceiveAsync(buffer.AsMemory(used), deadline.Token);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    used += part.Count;
                    if (part.MessageType != WebSocketMessageType.Text || used >= buffer.Length)
                        throw new InvalidDataException();
                } while (!part.EndOfMessage);
                if (DateTimeOffset.UtcNow - lastMessage < TimeSpan.FromSeconds(1)) throw new InvalidDataException();
                lastMessage = DateTimeOffset.UtcNow;
                var report = JsonSerializer.Deserialize<DeviceTelemetry>(buffer.AsSpan(0, used), WireJson.Options);
                if (report?.IsValid() != true) throw new InvalidDataException();
                await connections.Gate.WaitAsync(stop.Token);
                try
                {
                    if (!connections.Current(connection)) return;
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<BackendDbContext>();
                    if (!await Allowed(db, connection, stop.Token)) return;
                    var state = await db.Set<DeviceOperationalStatus>().FindAsync([connection.DeviceId], stop.Token);
                    if (state == null) { state = new() { DeviceId = connection.DeviceId }; db.Add(state); }
                    state.DeviceLastSeenAt = DateTimeOffset.UtcNow;
                    state.DeviceRuntimeState = report.RuntimeState;
                    state.DeviceFrameAgeMs = report.FrameAgeMs;
                    state.DeviceInstalledGalleryVersion = report.InstalledGalleryVersion;
                    state.DeviceModelSha256 = report.ModelSha256;
                    state.DeviceInstalledTemplateCount = report.InstalledTemplateCount;
                    state.DeviceOutboxPendingCount = report.OutboxPendingCount;
                    state.DeviceOutboxDeadCount = report.OutboxDeadCount;
                    await db.SaveChangesAsync(stop.Token);
                    connection.LastSeen = state.DeviceLastSeenAt;
                }
                finally { connections.Gate.Release(); }
                updates.DevicesChanged();
                connection.Notifications.Writer.TryWrite(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or InvalidDataException or JsonException) { }
        catch (Exception e)
        {
            loggers.CreateLogger("DeviceChannel").LogWarning("Device channel unavailable ({ErrorType})", e.GetType().Name);
        }
        finally
        {
            stop.Cancel();
            socket.Abort();
            connections.Remove(connection);
            if (sending != null) { try { await sending; } catch (Exception) { /* socket closed */ } }
            updates.DevicesChanged();
        }
    }

    private static Task<bool> Allowed(BackendDbContext db, DeviceConnection c, CancellationToken ct) =>
        db.Devices.AsNoTracking().AnyAsync(d => d.DeviceId == c.DeviceId && d.DeviceEnabled && d.DeviceTokenHash == c.CredentialHash, ct);

    private static async Task Send(DeviceConnection connection, CancellationToken ct)
    {
        try
        {
            await foreach (var reconcile in connection.Notifications.Reader.ReadAllAsync(ct))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var message = reconcile ? "{\"schema_version\":1,\"type\":\"reconcile\"}"u8.ToArray()
                    : "{\"schema_version\":1,\"type\":\"status_ack\"}"u8.ToArray();
                await connection.Socket.SendAsync(message.AsMemory(),
                    WebSocketMessageType.Text, true, deadline.Token);
            }
        }
        finally { connection.Socket.Abort(); }
    }
}
