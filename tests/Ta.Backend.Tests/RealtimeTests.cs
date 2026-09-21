using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ta.Backend.Common;
using Ta.Backend.Features.AcademicManagement;
using Ta.Backend.Features.Biometrics;
using Ta.Backend.Features.Devices;
using Ta.Backend.Features.Identity;
using Ta.Backend.Features.Realtime;

namespace Ta.Backend.Tests;

public sealed class RealtimeTests(BackendFixture fixture) : IClassFixture<BackendFixture>
{
    private async Task<(string Id, string Token)> Device()
    {
        var token = Credentials.Generate();
        var id = Guid.NewGuid().ToString("N");
        await using var db = fixture.OpenDatabase();
        db.Add(new Device { DeviceId = id, DeviceName = "Channel test", DeviceTokenHash = Credentials.Hash(token),
            DeviceCreatedAt = DateTimeOffset.UtcNow, DeviceCredentialChangedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return (id, token);
    }
    private async Task<WebSocket> Connect(string token, string path = "/api/v1/device-channel")
    {
        var client = fixture.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + token;
        return await client.ConnectAsync(new Uri("wss://localhost" + path), CancellationToken.None);
    }
    private static async Task<string> Read(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var bytes = new byte[4096];
        var result = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
        return Encoding.UTF8.GetString(bytes, 0, result.Count);
    }
    private static Task Send(WebSocket socket, object value) => socket.SendAsync(
        JsonSerializer.SerializeToUtf8Bytes(value, WireJson.Options).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None).AsTask();
    private DeviceTelemetry Telemetry(string gallery = "") => new(1, "status", "running", 0, gallery, fixture.ModelHash, 1, 3, 0);
    private async Task<JsonElement> Status(string id)
    {
        using var admin = fixture.Client(fixture.AdminToken);
        var response = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/devices/operational-status");
        return response.EnumerateArray().Single(s => s.GetProperty("device_id").GetString() == id);
    }

    [Fact]
    public async Task Installed_gallery_camera_storage_and_room_determine_readiness_latest_row_only()
    {
        var device = await Device();
        var version = "release-" + Guid.NewGuid().ToString("N");
        await using (var db = fixture.OpenDatabase())
        {
            var room = new Classroom { ClassroomCode = Guid.NewGuid().ToString("N"), ClassroomName = "Lab" };
            db.Add(room);
            db.Add(new DeviceRoomAssignment { DeviceId = device.Id, ClassroomId = room.ClassroomId, DeviceRoomAssignmentValidFrom = DateTimeOffset.UtcNow });
            db.Add(new GalleryRelease { GalleryVersion = version, GalleryModelSha256 = fixture.ModelHash,
                GalleryEtag = "\"" + new string('a', 64) + "\"", GalleryDocument = "{}", GalleryTemplateCount = 1, GalleryPublishedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using var socket = await Connect(device.Token);
        Assert.Contains("reconcile", await Read(socket));
        await Send(socket, Telemetry("downloaded-is-not-installed"));
        Assert.Contains("status_ack", await Read(socket));
        var status = await Status(device.Id);
        Assert.True(status.GetProperty("connected").GetBoolean());
        Assert.False(status.GetProperty("attendance_ready").GetBoolean());
        await Task.Delay(1050);
        await Send(socket, Telemetry(version));
        await Read(socket);
        Assert.True((await Status(device.Id)).GetProperty("attendance_ready").GetBoolean());
        await Task.Delay(1050);
        await Send(socket, Telemetry(version) with { FrameAgeMs = 30000, RuntimeState = "persistence_blocked" });
        await Read(socket);
        Assert.False((await Status(device.Id)).GetProperty("healthy").GetBoolean());
        await using var check = fixture.OpenDatabase();
        Assert.Equal(1, await check.Set<DeviceOperationalStatus>().CountAsync(s => s.DeviceId == device.Id));
        Assert.Equal(version, (await check.Set<DeviceOperationalStatus>().SingleAsync(s => s.DeviceId == device.Id)).DeviceInstalledGalleryVersion);
    }

    [Fact]
    public async Task Replacement_resets_readiness_and_rotation_disconnects_live_device()
    {
        var device = await Device();
        using var first = await Connect(device.Token);
        await Read(first);
        await Send(first, Telemetry());
        await Read(first);
        using var second = await Connect(device.Token);
        await Read(second);
        Assert.Equal("starting", (await Status(device.Id)).GetProperty("latest").GetProperty("device_runtime_state").GetString());
        using var admin = fixture.Client(fixture.AdminToken);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/admin/devices/{device.Id}/rotate-credential", null)).StatusCode);
        await Task.Delay(2500);
        Assert.False((await Status(device.Id)).GetProperty("connected").GetBoolean());
        await Assert.ThrowsAnyAsync<Exception>(() => Connect(device.Token));
    }

    [Fact]
    public async Task Gallery_commit_notifies_connected_device_and_reconnect_reconciles()
    {
        var device = await Device();
        using var socket = await Connect(device.Token);
        Assert.Contains("reconcile", await Read(socket));
        using var admin = fixture.Client(fixture.AdminToken);
        var release = new GalleryDocument(1, "notify-" + Guid.NewGuid().ToString("N"), "insightface/w600k_r50",
            fixture.ModelHash, 512, "f32le-base64", []);
        var response = await admin.PostAsJsonAsync("/api/v1/admin/gallery-releases", release, WireJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("reconcile", await Read(socket));
        using var edge = fixture.Client(device.Token);
        Assert.Contains(release.GalleryVersion, await edge.GetStringAsync("/api/v1/gallery"));
        using var next = await Connect(device.Token);
        Assert.Contains("reconcile", await Read(next));
    }

    [Fact]
    public async Task Malformed_or_private_telemetry_is_rejected_and_query_device_tokens_are_not_accepted()
    {
        var device = await Device();
        using var socket = await Connect(device.Token);
        await Read(socket);
        await Send(socket, new { schema_version = 1, type = "status", embedding = "must-not-store" });
        await Task.Delay(200);
        await using var db = fixture.OpenDatabase();
        Assert.False(await db.Set<DeviceOperationalStatus>().AnyAsync(s => s.DeviceId == device.Id));
        using var anonymous = fixture.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/device-channel?access_token=" + device.Token)).StatusCode);
        using var edge = fixture.Client(device.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await edge.GetAsync("/api/v1/admin/devices/operational-status")).StatusCode);
    }

    [Fact]
    public async Task Stale_connections_and_previous_server_snapshots_are_never_ready()
    {
        var device = await Device();
        using var socket = await Connect(device.Token);
        await Read(socket);
        await Send(socket, Telemetry());
        await Read(socket);
        // Expire the actual registry connection without a 45-second wall-clock test delay.
        var registry = fixture.Services.GetRequiredService<DeviceConnections>();
        using var stream = new MemoryStream();
        using var expired = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
        var replacement = new DeviceConnection(device.Id, Credentials.Hash(device.Token), expired);
        registry.Add(replacement);
        Assert.True(registry.Connected(device.Id));
        replacement.LastSeen = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.False((await Status(device.Id)).GetProperty("connected").GetBoolean());
        await using (var db = fixture.OpenDatabase()) await registry.Validate(db, CancellationToken.None);
        Assert.Equal(WebSocketState.Aborted, expired.State);
        registry.Remove(replacement);
        Assert.False((await Status(device.Id)).GetProperty("attendance_ready").GetBoolean());
    }

    [Fact]
    public async Task SignalR_is_human_only_role_scoped_and_revoked_sessions_are_disconnected()
    {
        async Task<(string Token, Guid Session)> Human(string role)
        {
            var token = Credentials.Generate();
            await using var db = fixture.OpenDatabase();
            var account = new Account { AccountEmail = Guid.NewGuid() + "@test.invalid", AccountName = "Test", AccountStatus = "approved", AccountRole = role, AccountPasswordHash = "unused" };
            var session = new AccountSession { AccountId = account.AccountId, AccountSessionTokenHash = Credentials.Hash(token), AccountSessionExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
            db.AddRange(account, session);
            await db.SaveChangesAsync();
            return (token, session.AccountSessionId);
        }
        var admin = await Human(Roles.Administrator);
        var student = await Human(Roles.Student);
        async Task<WebSocket> Hub(string token)
        {
            var socket = await Connect(token, "/api/v1/live");
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e").AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
            Assert.StartsWith("{}", await Read(socket));
            return socket;
        }
        using var a = await Hub(admin.Token);
        using var s = await Hub(student.Token);
        var updates = fixture.Services.GetRequiredService<RealtimeUpdates>();
        updates.DevicesChanged();
        Assert.Contains("devices", await Read(a));
        updates.AcademicCommitted();
        var studentUpdate = await Read(s);
        Assert.Contains("academic", studentUpdate);
        Assert.DoesNotContain("devices", studentUpdate);
        await using (var db = fixture.OpenDatabase())
            await db.Set<AccountSession>().Where(x => x.AccountSessionId == student.Session).ExecuteUpdateAsync(x => x.SetProperty(p => p.AccountSessionRevoked, true));
        await Task.Delay(2500);
        Assert.DoesNotContain(fixture.Services.GetRequiredService<PortalConnections>().Items.Values, c => c.SessionId == student.Session);
        using var http = fixture.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/auth/me?access_token=" + admin.Token)).StatusCode);
        var device = await Device();
        await Assert.ThrowsAnyAsync<Exception>(() => Hub(device.Token));
    }
}
