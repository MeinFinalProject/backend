using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.Attendance;
using Ta.Backend.Features.Audit;
using Ta.Backend.Features.Identity;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.AcademicManagement;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/teaching").WithTags("Teaching Sessions").RequireAuthorization("Academic")
            .AddEndpointFilter<AcademicMutationFilter>();
        group.MapGet("/sessions", async (Guid? classId, DateTimeOffset? from, DateTimeOffset? until,
            AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var query = db.Set<TeachingSession>().AsNoTracking();
            if (!access.IsAdmin)
            {
                if (access.User.IsInRole(Roles.Lecturer))
                {
                    var lecturer = await access.Lecturer(ct);
                    query = query.Where(s => s.LecturerId == lecturer.LecturerId);
                }
                else
                {
                    var student = await access.Student(ct);
                    query = query.Where(s => db.Set<ClassMembership>().Any(m => m.StudentId == student.StudentId && m.AcademicClassId == s.AcademicClassId
                        && m.ClassMembershipValidFrom <= s.TeachingSessionStart && (m.ClassMembershipValidUntil == null || m.ClassMembershipValidUntil > s.TeachingSessionStart)));
                }
            }
            if (classId is not null) query = query.Where(s => s.AcademicClassId == classId);
            if (from is not null) query = query.Where(s => s.TeachingSessionStart >= from);
            if (until is not null) query = query.Where(s => s.TeachingSessionStart < until);
            return await query.OrderBy(s => s.TeachingSessionStart).Take(500).ToListAsync(ct);
        });
        group.MapPost("/classes/{classId:guid}/sessions", async (Guid classId, SessionRequest request, AcademicAccess access, SessionService service, BackendDbContext db, CancellationToken ct) =>
        {
            var academicClass = await access.Class(classId, ct);
            if (request.ReplacesSessionId is not null)
            {
                var original = await access.Session(request.ReplacesSessionId.Value, ct);
                DomainException.Require(original.AcademicClassId == classId && original.TeachingSessionStatus == "cancelled", "replacement_requires_cancelled_session");
                DomainException.Require(!await db.Set<TeachingSession>().AnyAsync(s => s.ReplacesTeachingSessionId == original.TeachingSessionId && s.TeachingSessionStatus != "cancelled", ct), "replacement_already_exists", 409);
            }
            return Results.Ok(await service.Create(academicClass, request, ct));
        }).RequireAuthorization(Roles.StaffPolicy).Produces<TeachingSession>();
        group.MapPut("/sessions/{id:guid}", async (Guid id, SessionRequest request, AcademicAccess access, SessionService service, CancellationToken ct) =>
        {
            var session = await access.Session(id, ct);
            DomainException.Require(request.Revision == session.TeachingSessionRevision, "stale_session_revision", 409);
            DomainException.Require(request.ReplacesSessionId == session.ReplacesTeachingSessionId, "replacement_reference_is_immutable");
            await service.Apply(session, request, ct);
            session.TeachingSessionRevision++;
            return Results.Ok(session);
        }).RequireAuthorization(Roles.StaffPolicy).Produces<TeachingSession>();
        group.MapPost("/sessions/{id:guid}/cancel", async (Guid id, ReviewNote request, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var session = await access.Session(id, ct);
            DomainException.Require(session.TeachingSessionStatus == "scheduled" && session.TeachingSessionStart.AddMinutes(-session.TeachingSessionEarlyMinutes) > DateTimeOffset.UtcNow,
                "session_window_already_started", 409);
            DomainException.Require(WireJson.Identifier(request.Note), "reason_required");
            session.TeachingSessionStatus = "cancelled";
            session.TeachingSessionRevision++;
            AuditLog.Add(db, access.User, "cancel_session", id.ToString(), request);
            return Results.NoContent();
        }).RequireAuthorization(Roles.StaffPolicy);
        group.MapPost("/sessions/{id:guid}/close", async (Guid id, AcademicAccess access, SessionService service, BackendDbContext db, CancellationToken ct) =>
        {
            var session = await access.Session(id, ct);
            DomainException.Require(session.TeachingSessionStatus != "cancelled" && session.TeachingSessionEnd <= DateTimeOffset.UtcNow, "session_not_finished", 409);
            await service.FreezeRoster(session, ct);
            session.TeachingSessionStatus = "closed";
            return Results.NoContent();
        }).RequireAuthorization(Roles.StaffPolicy);

        group.MapGet("/classes/{classId:guid}/schedules", async (Guid classId, BackendDbContext db, CancellationToken ct) =>
            await db.Set<AcademicSchedule>().Where(s => s.AcademicClassId == classId).OrderBy(s => s.AcademicScheduleValidFrom).ToListAsync(ct));
        group.MapPost("/classes/{classId:guid}/schedules", async (Guid classId, ScheduleRequest request, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var academicClass = await access.Class(classId, ct);
            var term = await db.Set<AcademicTerm>().SingleAsync(t => t.AcademicTermId == academicClass.AcademicTermId, ct);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Jakarta")).DateTime);
            DomainException.Require(request.DayOfWeek is >= 0 and <= 6 && request.End > request.Start && request.ValidFrom >= today
                && request.ValidUntil >= request.ValidFrom && request.ValidFrom >= term.AcademicTermStart && request.ValidUntil <= term.AcademicTermEnd,
                "invalid_schedule");
            DomainException.Require(await db.Set<Classroom>().AnyAsync(r => r.ClassroomId == request.ClassroomId && r.ClassroomActive, ct), "invalid_classroom");
            if (request.SupersedesScheduleId is not null)
            {
                var previous = await db.Set<AcademicSchedule>().FindAsync([request.SupersedesScheduleId.Value], ct)
                    ?? throw new DomainException("schedule_not_found", 404);
                DomainException.Require(previous.AcademicClassId == classId && request.ValidFrom > previous.AcademicScheduleValidFrom, "invalid_schedule_replacement");
                DomainException.Require(!await db.Set<TeachingSession>().AnyAsync(s => s.AcademicScheduleId == previous.AcademicScheduleId
                    && s.TeachingSessionStatus != "cancelled" && s.TeachingSessionStart >= new DateTimeOffset(request.ValidFrom.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7)).ToUniversalTime(), ct),
                    "update_or_cancel_generated_sessions_first", 409);
                previous.AcademicScheduleValidUntil = request.ValidFrom.AddDays(-1);
            }
            var overlaps = await db.Set<AcademicSchedule>().Where(s => s.AcademicClassId == classId && s.AcademicScheduleDayOfWeek == request.DayOfWeek
                && s.AcademicScheduleId != request.SupersedesScheduleId && s.AcademicScheduleValidUntil >= request.ValidFrom && s.AcademicScheduleValidFrom <= request.ValidUntil
                && s.AcademicScheduleStart < request.End && s.AcademicScheduleEnd > request.Start).AnyAsync(ct);
            DomainException.Require(!overlaps, "recurring_schedule_conflict", 409);
            var schedule = new AcademicSchedule { AcademicClassId = classId, ClassroomId = request.ClassroomId, AcademicScheduleDayOfWeek = request.DayOfWeek,
                AcademicScheduleStart = request.Start, AcademicScheduleEnd = request.End, AcademicScheduleValidFrom = request.ValidFrom, AcademicScheduleValidUntil = request.ValidUntil };
            db.Add(schedule);
            return Results.Ok(schedule);
        }).RequireAuthorization(Roles.StaffPolicy);
        group.MapPost("/schedules/{id:guid}/generate", async (Guid id, GenerateSessions request, AcademicAccess access, SessionService service, BackendDbContext db, CancellationToken ct) =>
        {
            var schedule = await db.Set<AcademicSchedule>().FindAsync([id], ct) ?? throw new DomainException("schedule_not_found", 404);
            var academicClass = await access.Class(schedule.AcademicClassId, ct);
            DomainException.Require(request.From >= schedule.AcademicScheduleValidFrom && request.Until <= schedule.AcademicScheduleValidUntil
                && request.Until >= request.From && request.Until.DayNumber - request.From.DayNumber <= 180, "invalid_generation_range");
            var ids = new List<Guid>();
            for (var day = request.From; day <= request.Until; day = day.AddDays(1))
            {
                if ((int)day.DayOfWeek != schedule.AcademicScheduleDayOfWeek) continue;
                var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.AcademicScheduleTimeZone);
                var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(schedule.AcademicScheduleStart), zone));
                var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(schedule.AcademicScheduleEnd), zone));
                var existing = await db.Set<TeachingSession>().Where(s => s.AcademicClassId == schedule.AcademicClassId && s.TeachingSessionStart == start
                    && (s.TeachingSessionStatus != "cancelled" || s.AcademicScheduleId == id))
                    .OrderBy(s => s.TeachingSessionStatus == "cancelled").FirstOrDefaultAsync(ct);
                if (existing is not null) { ids.Add(existing.TeachingSessionId); continue; }
                var session = await service.Create(academicClass, new SessionRequest(schedule.ClassroomId, start, end,
                    request.EarlyMinutes, request.LateMinutes, request.CheckinMinutes), ct);
                session.AcademicScheduleId = id;
                await db.SaveChangesAsync(ct);
                ids.Add(session.TeachingSessionId);
            }
            return Results.Ok(new { teaching_session_ids = ids });
        }).RequireAuthorization(Roles.StaffPolicy);
    }
}
public sealed record ScheduleRequest(Guid ClassroomId, int DayOfWeek, TimeOnly Start, TimeOnly End,
    DateOnly ValidFrom, DateOnly ValidUntil, Guid? SupersedesScheduleId = null);
public sealed record GenerateSessions(DateOnly From, DateOnly Until, int EarlyMinutes = 15, int LateMinutes = 10, int CheckinMinutes = 60);
