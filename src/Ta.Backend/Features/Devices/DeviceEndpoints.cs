using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ta.Backend.Common;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Devices;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/devices").WithTags("Devices").RequireAuthorization(Credentials.AdminScheme)
            .AddEndpointFilter<AcademicMutationFilter>();
        group.MapGet("/", async (BackendDbContext db, CancellationToken ct) =>
            await db.Devices.OrderBy(d => d.DeviceId).Select(d => new DeviceSummary(
                d.DeviceId, d.DeviceName, d.DeviceEnabled, d.DeviceCreatedAt, d.DeviceCredentialChangedAt)).ToListAsync(ct))
            .WithName("ListDevices").WithSummary("List registered devices")
            .Produces<List<DeviceSummary>>();

        group.MapPost("/", async (CreateDevice request, BackendDbContext db, HttpContext context, CancellationToken ct) =>
        {
            if (!WireJson.Identifier(request.DeviceId) || string.IsNullOrWhiteSpace(request.DeviceName)
                || request.DeviceName.Length > 200) return Results.BadRequest(new { error = "invalid_device" });
            var token = Credentials.Generate();
            var now = DateTimeOffset.UtcNow;
            if (await db.Devices.AnyAsync(d => d.DeviceId == request.DeviceId, ct)) return Results.Conflict(new { error = "device_exists" });
            db.Devices.Add(new Device
            {
                DeviceId = request.DeviceId,
                DeviceName = request.DeviceName,
                DeviceTokenHash = Credentials.Hash(token),
                DeviceCreatedAt = now,
                DeviceCredentialChangedAt = now
            });
            await db.SaveChangesAsync(ct);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Created($"{ApiRoutes.V1}/admin/devices/{Uri.EscapeDataString(request.DeviceId)}", new DeviceCredential(request.DeviceId, token));
        }).WithName("RegisterDevice").WithSummary("Register a device and issue its credential")
            .WithDescription("Returns the plaintext credential once. Store it securely on the Edge device.")
            .Produces<DeviceCredential>(StatusCodes.Status201Created)
            .Produces<ApiError>(StatusCodes.Status400BadRequest).Produces<ApiError>(StatusCodes.Status409Conflict);

        group.MapPost("/{deviceId}/rotate-credential", async (string deviceId, BackendDbContext db, HttpContext context, CancellationToken ct) =>
        {
            var token = Credentials.Generate();
            var updated = await db.Devices.Where(d => d.DeviceId == deviceId).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.DeviceTokenHash, Credentials.Hash(token))
                .SetProperty(d => d.DeviceCredentialChangedAt, DateTimeOffset.UtcNow), ct);
            context.Response.Headers.CacheControl = "no-store";
            return updated == 0 ? Results.NotFound() : Results.Ok(new DeviceCredential(deviceId, token));
        }).WithName("RotateDeviceCredential").WithSummary("Replace a device credential")
            .WithDescription("Invalidates the previous credential for subsequent requests. Does not enable a disabled device.")
            .Produces<DeviceCredential>().Produces(StatusCodes.Status404NotFound);

        group.MapPut("/{deviceId}/status", async (string deviceId, DeviceStatus request, BackendDbContext db, CancellationToken ct) =>
        {
            var updated = await db.Devices.Where(d => d.DeviceId == deviceId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeviceEnabled, request.Enabled), ct);
            return updated == 0 ? Results.NotFound() : Results.NoContent();
        }).WithName("SetDeviceStatus").WithSummary("Enable or disable a device")
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);
    }
}

public sealed record CreateDevice(string DeviceId, string DeviceName);
public sealed record DeviceStatus(bool Enabled);
public sealed record DeviceCredential(string DeviceId, string Token);
public sealed record DeviceSummary(string DeviceId, string DeviceName, bool DeviceEnabled,
    DateTimeOffset DeviceCreatedAt, DateTimeOffset DeviceCredentialChangedAt);
