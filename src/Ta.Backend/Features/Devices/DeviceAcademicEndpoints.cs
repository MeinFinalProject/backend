using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.AcademicManagement;
using Ta.Backend.Features.Audit;
using Ta.Backend.Features.Identity;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Devices;

public static class DeviceAcademicEndpoints
{
    public static void MapDeviceAcademicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/devices").WithTags("Devices").RequireAuthorization(Roles.AdministratorPolicy)
            .AddEndpointFilter<AcademicMutationFilter>();
        group.MapPut("/{deviceId}/classroom", async (string deviceId, AssignRoom request, BackendDbContext db, HttpContext context, CancellationToken ct) =>
        {
            DomainException.Require(await db.Devices.AnyAsync(d => d.DeviceId == deviceId, ct), "device_not_found", 404);
            DomainException.Require(request.ClassroomId == null || await db.Set<Classroom>().AnyAsync(r => r.ClassroomId == request.ClassroomId && r.ClassroomActive, ct), "invalid_classroom");
            var current = await db.Set<DeviceRoomAssignment>().SingleOrDefaultAsync(a => a.DeviceId == deviceId && a.DeviceRoomAssignmentValidUntil == null, ct);
            if (current?.ClassroomId == request.ClassroomId) return Results.NoContent();
            var now = DateTimeOffset.UtcNow;
            if (current is not null) { current.DeviceRoomAssignmentValidUntil = now; await db.SaveChangesAsync(ct); }
            if (request.ClassroomId.HasValue) db.Add(new DeviceRoomAssignment { DeviceId = deviceId, ClassroomId = request.ClassroomId.Value, DeviceRoomAssignmentValidFrom = now });
            AuditLog.Add(db, context.User, "device.assign_room", deviceId, new { previous_classroom_id = current?.ClassroomId, classroom_id = request.ClassroomId });
            return Results.NoContent();
        });
        group.MapGet("/{deviceId}/activity", async (string deviceId, BackendDbContext db, CancellationToken ct) =>
        {
            DomainException.Require(await db.Devices.AnyAsync(d => d.DeviceId == deviceId, ct), "device_not_found", 404);
            var latest = await db.AttendanceEvents.AsNoTracking().Where(e => e.DeviceId == deviceId).OrderByDescending(e => e.AttendanceReceivedAt)
                .Select(e => new { e.AttendanceReceivedAt, e.AttendanceOccurredAt, e.AttendanceGalleryVersion }).FirstOrDefaultAsync(ct);
            var assignments = await db.Set<DeviceRoomAssignment>().AsNoTracking().Where(a => a.DeviceId == deviceId).OrderByDescending(a => a.DeviceRoomAssignmentValidFrom).Take(100).ToListAsync(ct);
            return Results.Ok(new { device_id = deviceId, latest_received_observation = latest, room_assignments = assignments,
                heartbeat_supported = true, gallery_acknowledgement_supported = true });
        });
    }
}
public sealed record AssignRoom(Guid? ClassroomId);
