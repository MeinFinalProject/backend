using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.Attendance;
using Ta.Backend.Features.Biometrics;
using Ta.Backend.Features.Devices;

namespace Ta.Backend.Tests;

public sealed class EdgeIntegrationTests(BackendFixture fixture) : IClassFixture<BackendFixture>
{
    private async Task<(string Id, string Token)> Device()
    {
        var id = "device-" + Guid.NewGuid().ToString("N");
        using var admin = fixture.Client(fixture.AdminToken);
        var response = await admin.PostAsJsonAsync("/api/v1/admin/devices", new CreateDevice(id, "Test device"), WireJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/v1/admin/devices/{id}", response.Headers.Location!.ToString());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (id, json.GetProperty("token").GetString()!);
    }

    private static AttendanceObservation Observation(string device) => new(1, Guid.CreateVersion7().ToString(),
        DateTimeOffset.Parse("2026-01-01T10:00:00.123Z"), device, "identity-not-yet-enrolled", "offline-gallery-1",
        "edge-v1", ulong.MaxValue.ToString(), "9007199254740993", .75, .1, .99);

    private static Task<HttpResponseMessage> Send(HttpClient client, string device, params AttendanceObservation[] events) =>
        client.PostAsJsonAsync("/api/v1/attendance-events/batch", new { schema_version = 1, device_id = device, events }, WireJson.Options);

    private static async Task<string[]> Statuses(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("schema_version").GetInt32());
        return json.GetProperty("results").EnumerateArray().Select(e => e.GetProperty("status").GetString()!).ToArray();
    }

    [Fact]
    public async Task Authentication_rotation_disable_and_role_separation()
    {
        var device = await Device();
        using var anonymous = fixture.Client();
        using var edge = fixture.Client(device.Token);
        using var admin = fixture.Client(fixture.AdminToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/gallery")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await edge.GetAsync("/api/v1/admin/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/v1/gallery")).StatusCode);
        var rotate = await admin.PostAsync($"/api/v1/admin/devices/{device.Id}/rotate-credential", null);
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode);
        var token = (await rotate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await edge.GetAsync("/api/v1/gallery")).StatusCode);
        using var rotated = fixture.Client(token);
        Assert.Equal(["accepted"], await Statuses(await Send(rotated, device.Id, Observation(device.Id))));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/v1/admin/devices/{device.Id}/status", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rotated.GetAsync("/api/v1/gallery")).StatusCode);
        await using var db = fixture.OpenDatabase();
        var stored = await db.Devices.SingleAsync(d => d.DeviceId == device.Id);
        Assert.Equal(Credentials.Hash(token), stored.DeviceTokenHash);
        var listing = await admin.GetStringAsync("/api/v1/admin/devices");
        Assert.DoesNotContain(token, listing);
        Assert.DoesNotContain("token_hash", listing);
    }

    [Fact]
    public async Task Attendance_retry_is_durable_and_preserves_uint64_strings()
    {
        var device = await Device();
        var observation = Observation(device.Id);
        using var edge = fixture.Client(device.Token);
        Assert.Equal(["accepted"], await Statuses(await Send(edge, device.Id, observation)));
        // A fresh client/request scope simulates a lost acknowledgement and reconnect.
        using var retryClient = fixture.Client(device.Token);
        Assert.Equal(["duplicate"], await Statuses(await Send(retryClient, device.Id, observation)));
        Assert.Equal(["rejected"], await Statuses(await Send(edge, device.Id, observation with { Similarity = .8 })));
        await using var db = fixture.OpenDatabase();
        var id = Guid.Parse(observation.EventId);
        Assert.Equal(1, await db.AttendanceEvents.CountAsync(e => e.AttendanceEventId == id));
        var stored = await db.AttendanceEvents.SingleAsync(e => e.AttendanceEventId == id);
        var payload = JsonDocument.Parse(stored.AttendancePayload).RootElement;
        Assert.Equal(ulong.MaxValue.ToString(), payload.GetProperty("track_id").GetString());
        Assert.Equal("9007199254740993", payload.GetProperty("camera_frame_id").GetString());
    }

    [Fact]
    public async Task Concurrent_replays_insert_exactly_once()
    {
        var device = await Device();
        using var edge = fixture.Client(device.Token);
        var observation = Observation(device.Id);
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Send(edge, device.Id, observation)));
        var statuses = (await Task.WhenAll(responses.Select(Statuses))).SelectMany(x => x).ToArray();
        Assert.Equal(1, statuses.Count(s => s == "accepted"));
        Assert.Equal(11, statuses.Count(s => s == "duplicate"));
    }

    [Fact]
    public async Task Overlapping_batches_in_reverse_order_do_not_deadlock()
    {
        var device = await Device();
        using var edge = fixture.Client(device.Token);
        var a = Observation(device.Id);
        var b = Observation(device.Id);
        var responses = await Task.WhenAll(Send(edge, device.Id, a, b), Send(edge, device.Id, b, a));
        var statuses = (await Task.WhenAll(responses.Select(Statuses))).SelectMany(x => x).ToArray();
        Assert.Equal(2, statuses.Count(s => s == "accepted"));
        Assert.Equal(2, statuses.Count(s => s == "duplicate"));
    }

    [Fact]
    public async Task Mixed_batch_rejects_only_invalid_events()
    {
        var device = await Device();
        using var edge = fixture.Client(device.Token);
        var good = Observation(device.Id);
        var invalid = Observation(device.Id) with { PadMedianPReal = 3 };
        var statuses = await Statuses(await Send(edge, device.Id, good, invalid));
        Assert.Contains("accepted", statuses);
        Assert.Contains("rejected", statuses);
        Assert.Equal(["duplicate"], await Statuses(await Send(edge, device.Id, good)));
    }

    [Fact]
    public async Task Device_spoofing_and_cross_device_event_id_collision_are_denied()
    {
        var first = await Device();
        var second = await Device();
        using var edge = fixture.Client(first.Token);
        var original = Observation(first.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(edge, second.Id, original)).StatusCode);
        Assert.Equal(["rejected"], await Statuses(await Send(edge, first.Id, original with { DeviceId = second.Id })));
        Assert.Equal(["accepted"], await Statuses(await Send(edge, first.Id, original)));
        using var other = fixture.Client(second.Token);
        Assert.Equal(["rejected"], await Statuses(await Send(other, second.Id, original with { DeviceId = second.Id })));
    }

    [Fact]
    public async Task Invalid_envelopes_are_rejected_without_partial_writes()
    {
        var device = await Device();
        using var edge = fixture.Client(device.Token);
        var observation = Observation(device.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(edge, device.Id, observation, observation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(edge, device.Id)).StatusCode);
        var invalidVersion = await edge.PostAsJsonAsync("/api/v1/attendance-events/batch", new { schema_version = 2, device_id = device.Id, events = new[] { observation } }, WireJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, invalidVersion.StatusCode);
        await using var db = fixture.OpenDatabase();
        Assert.False(await db.AttendanceEvents.AnyAsync(e => e.DeviceId == device.Id));
    }

    [Fact]
    public async Task Missing_numeric_field_is_rejected_instead_of_becoming_zero()
    {
        var device = await Device();
        using var edge = fixture.Client(device.Token);
        var element = JsonSerializer.SerializeToNode(Observation(device.Id), WireJson.Options)!.AsObject();
        element.Remove("similarity");
        var response = await edge.PostAsJsonAsync("/api/v1/attendance-events/batch", new { schema_version = 1, device_id = device.Id, events = new[] { element } });
        Assert.Equal(["rejected"], await Statuses(response));
    }

    [Fact]
    public async Task Unknown_schema_fields_are_rejected_and_formatting_does_not_change_identity()
    {
        var device = await Device();
        using var edge = fixture.Client(device.Token);
        var observation = Observation(device.Id);
        Assert.Equal(["accepted"], await Statuses(await Send(edge, device.Id, observation)));
        var element = JsonSerializer.SerializeToNode(observation, WireJson.Options)!.AsObject();
        var reordered = new System.Text.Json.Nodes.JsonObject();
        foreach (var pair in element.Reverse()) reordered[pair.Key] = pair.Value?.DeepClone();
        var response = await edge.PostAsJsonAsync("/api/v1/attendance-events/batch", new { schema_version = 1, device_id = device.Id, events = new[] { reordered } });
        Assert.Equal(["duplicate"], await Statuses(response));
        element["unrecognized_field"] = true;
        response = await edge.PostAsJsonAsync("/api/v1/attendance-events/batch", new { schema_version = 1, device_id = device.Id, events = new[] { element } });
        Assert.Equal(["rejected"], await Statuses(response));
    }

    [Fact]
    public async Task Gallery_publication_round_trip_etag_and_immutable_versions()
    {
        var device = await Device();
        using var edge = fixture.Client(device.Token);
        using var admin = fixture.Client(fixture.AdminToken);
        var unavailable = await edge.GetAsync("/api/v1/gallery");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.NotNull(unavailable.Headers.RetryAfter);
        var bytes = new byte[2048];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes, 1f);
        var document = new GalleryDocument(1, "gallery-test-1", "insightface/w600k_r50", fixture.ModelHash,
            512, "f32le-base64", [new GalleryTemplate("template-1", "identity-1", 512, "f32le-base64", Convert.ToBase64String(bytes))]);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/admin/gallery-releases", document, WireJson.Options)).StatusCode);
        var response = await edge.GetAsync("/api/v1/gallery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var downloaded = await response.Content.ReadFromJsonAsync<GalleryDocument>(WireJson.Options);
        Assert.NotNull(downloaded);
        Assert.Null(downloaded.Validate(fixture.ModelHash));
        Assert.Equal(document.Templates[0].Data, downloaded.Templates[0].Data);
        var etag = response.Headers.ETag!.ToString();
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/v1/gallery");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", $"\"unrelated\", W/{etag}");
        var unchanged = await edge.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/admin/gallery-releases", document, WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/admin/gallery-releases", document with { Templates = [] }, WireJson.Options)).StatusCode);
        // Empty releases explicitly revoke every template; old conditional request must get 200.
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/admin/gallery-releases", document with { GalleryVersion = "gallery-test-2", Templates = [] }, WireJson.Options)).StatusCode);
        using var changed = new HttpRequestMessage(HttpMethod.Get, "/api/v1/gallery");
        changed.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.OK, (await edge.SendAsync(changed)).StatusCode);
    }

    [Fact]
    public async Task Health_and_https_policy()
    {
        using var client = fixture.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("http://localhost/health/live")).StatusCode);
    }

    [Fact]
    public async Task Database_failure_rolls_back_entire_batch_without_acknowledging()
    {
        var device = await Device();
        using var edge = fixture.Client(device.Token);
        await using var db = fixture.OpenDatabase();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE attendance_event ADD CONSTRAINT ck_test_rollback CHECK (attendance_identity_id <> 'force-rollback')");
        try
        {
            var good = Observation(device.Id) with { EventId = "00000000-0000-7000-8000-000000000001" };
            var failed = Observation(device.Id) with { EventId = "ffffffff-ffff-7fff-bfff-ffffffffffff", IdentityId = "force-rollback" };
            var response = await Send(edge, device.Id, good, failed);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.NotNull(response.Headers.RetryAfter);
            Assert.DoesNotContain("results", await response.Content.ReadAsStringAsync());
            Assert.False(await db.AttendanceEvents.AnyAsync(e => e.DeviceId == device.Id));
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE attendance_event DROP CONSTRAINT ck_test_rollback"); }
    }

    [Fact]
    public async Task Migrations_match_model_and_apply_idempotently()
    {
        await using var db = fixture.OpenDatabase();
        Assert.False(db.Database.HasPendingModelChanges());
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Malformed_json_does_not_expose_diagnostics()
    {
        var device = await Device();
        using var edge = fixture.Client(device.Token);
        var response = await edge.PostAsync("/api/v1/attendance-events/batch", new StringContent("{invalid", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("Exception", await response.Content.ReadAsStringAsync());
    }
}
