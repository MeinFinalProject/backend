using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.AcademicManagement;
using Ta.Backend.Features.Audit;
using Ta.Backend.Features.Identity;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Attendance;

public static class AcademicAttendanceEndpoints
{
    public static void MapAcademicAttendanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/attendance").WithTags("Academic attendance")
            .RequireAuthorization("Academic").AddEndpointFilter<AcademicMutationFilter>();
        group.MapGet("/sessions/{id:guid}", async (Guid id, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var session = await access.Session(id, ct);
            var students = await Roster(db, session).ToListAsync(ct);
            var records = await db.Set<SessionAttendance>().Where(a => a.TeachingSessionId == id).ToDictionaryAsync(a => a.StudentId, ct);
            var identities = await (from s in db.Set<Student>() join a in db.Set<Account>() on s.AccountId equals a.AccountId
                where students.Contains(s.StudentId) select new { s.StudentId, s.StudentNumber, a.AccountName }).ToListAsync(ct);
            return Results.Ok(new { session, students = identities.Select(s => new { student = s, attendance = records.GetValueOrDefault(s.StudentId),
                status = records.GetValueOrDefault(s.StudentId)?.SessionAttendanceStatus ?? (session.TeachingSessionEnd <= DateTimeOffset.UtcNow ? "absent" : "pending") }) });
        }).RequireAuthorization(Roles.StaffPolicy);

        group.MapPut("/sessions/{id:guid}/students/{studentId:guid}", async (Guid id, Guid studentId, ManualAttendance request,
            AcademicAccess access, BackendDbContext db, SessionService sessions, CancellationToken ct) =>
        {
            var session = await access.Session(id, ct);
            DomainException.Require(session.TeachingSessionStatus != "cancelled" && session.TeachingSessionStart <= DateTimeOffset.UtcNow, "session_not_started_or_cancelled", 409);
            await sessions.FreezeRoster(session, ct);
            DomainException.Require(await Roster(db, session).ContainsAsync(studentId, ct)
                || db.Set<SessionRoster>().Local.Any(r => r.TeachingSessionId == id && r.StudentId == studentId), "student_not_in_session_roster", 409);
            DomainException.Require(request.Status is "present" or "late" or "absent" or "excused" && WireJson.Identifier(request.Reason), "invalid_attendance_correction");
            DomainException.Require(request.OccurredAt == null || (request.OccurredAt >= session.TeachingSessionStart.AddMinutes(-session.TeachingSessionEarlyMinutes)
                && request.OccurredAt < session.TeachingSessionEnd), "invalid_checkin_time");
            var record = await db.Set<SessionAttendance>().SingleOrDefaultAsync(a => a.TeachingSessionId == id && a.StudentId == studentId, ct);
            DomainException.Require(request.Revision == (record?.SessionAttendanceRevision ?? 0), "attendance_revision_conflict", 409);
            var before = record is null ? null : new { record.SessionAttendanceStatus, record.SessionAttendanceSource, record.SessionAttendanceOccurredAt, record.AttendanceEventId };
            if (record is null) { record = new SessionAttendance { TeachingSessionId = id, StudentId = studentId }; db.Add(record); }
            else record.SessionAttendanceRevision++;
            record.SessionAttendanceStatus = request.Status;
            record.SessionAttendanceSource = "manual";
            record.SessionAttendanceOccurredAt = request.Status is "present" or "late" ? request.OccurredAt?.ToUniversalTime() : null;
            record.SessionAttendanceLateMinutes = record.SessionAttendanceOccurredAt is {} at ? Math.Max(0, (decimal)(at - session.TeachingSessionStart).TotalMinutes) : 0;
            record.SessionAttendanceUpdatedAt = DateTimeOffset.UtcNow;
            AuditLog.Add(db, access.User, "attendance.correct", record.SessionAttendanceId.ToString(), new { before, after = request });
            return Results.Ok(record);
        }).RequireAuthorization(Roles.StaffPolicy);

        group.MapGet("/students/{studentId:guid}", async (Guid studentId, Guid? classId, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            if (access.User.IsInRole(Roles.Student)) DomainException.Require((await access.Student(ct)).StudentId == studentId, "own_attendance_only", 403);
            var now = DateTimeOffset.UtcNow;
            var query = db.Set<TeachingSession>().Where(s => s.TeachingSessionStatus != "cancelled"
                && s.TeachingSessionStart.AddMinutes(-s.TeachingSessionEarlyMinutes) <= now);
            if (classId.HasValue) query = query.Where(s => s.AcademicClassId == classId.Value);
            if (access.User.IsInRole(Roles.Lecturer))
            {
                var lecturer = await access.Lecturer(ct);
                query = query.Where(s => s.LecturerId == lecturer.LecturerId);
            }
            query = query.Where(s => db.Set<SessionRoster>().Any(r => r.TeachingSessionId == s.TeachingSessionId && r.StudentId == studentId)
                || db.Set<ClassMembership>().Any(m => m.StudentId == studentId && m.AcademicClassId == s.AcademicClassId
                    && m.ClassMembershipValidFrom <= s.TeachingSessionStart && (m.ClassMembershipValidUntil == null || m.ClassMembershipValidUntil > s.TeachingSessionStart)));
            var sessions = await query.OrderBy(s => s.TeachingSessionStart).ToListAsync(ct);
            var ids = sessions.Select(s => s.TeachingSessionId).ToArray();
            var records = await db.Set<SessionAttendance>().Where(a => a.StudentId == studentId && ids.Contains(a.TeachingSessionId)).ToDictionaryAsync(a => a.TeachingSessionId, ct);
            var classes = await db.Set<AcademicClass>().Where(c => sessions.Select(s => s.AcademicClassId).Distinct().ToArray().Contains(c.AcademicClassId)).ToListAsync(ct);
            var terms = await db.Set<AcademicTerm>().ToDictionaryAsync(t => t.AcademicTermId, ct);
            var summaries = sessions.Where(s => s.TeachingSessionEnd <= now).GroupBy(s => s.AcademicClassId).Select(g =>
            {
                var statuses = g.Select(s => records.GetValueOrDefault(s.TeachingSessionId)?.SessionAttendanceStatus ?? "absent").ToArray();
                var threshold = terms[classes.Single(c => c.AcademicClassId == g.Key).AcademicTermId].AcademicTermMinimumAttendance;
                return AttendanceReporting.Summarize(g.Key, statuses, threshold);
            });
            return Results.Ok(new { student_id = studentId, summaries,
                sessions = sessions.Where(s => s.TeachingSessionEnd <= now).Select(s => new { session = s,
                    status = records.GetValueOrDefault(s.TeachingSessionId)?.SessionAttendanceStatus ?? "absent", attendance = records.GetValueOrDefault(s.TeachingSessionId) }),
                ongoing_sessions = sessions.Where(s => s.TeachingSessionEnd > now).Select(s => new { session = s,
                    status = records.GetValueOrDefault(s.TeachingSessionId)?.SessionAttendanceStatus ?? "pending", attendance = records.GetValueOrDefault(s.TeachingSessionId) }) });
        });
        group.MapGet("/classes/{id:guid}/summary", async (Guid id, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var academicClass = await access.Class(id, ct);
            var term = await db.Set<AcademicTerm>().SingleAsync(t => t.AcademicTermId == academicClass.AcademicTermId, ct);
            var sessions = await db.Set<TeachingSession>().Where(s => s.AcademicClassId == id && s.TeachingSessionStatus != "cancelled"
                && s.TeachingSessionEnd <= DateTimeOffset.UtcNow).ToListAsync(ct);
            var sessionIds = sessions.Select(s => s.TeachingSessionId).ToArray();
            var memberships = await db.Set<ClassMembership>().Where(m => m.AcademicClassId == id).ToListAsync(ct);
            var roster = await db.Set<SessionRoster>().Where(r => sessionIds.Contains(r.TeachingSessionId)).ToListAsync(ct);
            var records = await db.Set<SessionAttendance>().Where(a => sessionIds.Contains(a.TeachingSessionId)).ToDictionaryAsync(a => (a.StudentId, a.TeachingSessionId), ct);
            var studentIds = memberships.Select(m => m.StudentId).Union(roster.Select(r => r.StudentId)).ToArray();
            var students = await (from s in db.Set<Student>() join a in db.Set<Account>() on s.AccountId equals a.AccountId
                where studentIds.Contains(s.StudentId) orderby s.StudentNumber select new { s.StudentId, s.StudentNumber, a.AccountName }).ToListAsync(ct);
            return Results.Ok(students.Select(s => new { student = s, summary = AttendanceReporting.Summarize(id,
                sessions.Where(session => roster.Any(r => r.StudentId == s.StudentId && r.TeachingSessionId == session.TeachingSessionId)
                    || memberships.Any(m => m.StudentId == s.StudentId && m.ClassMembershipValidFrom <= session.TeachingSessionStart
                        && (m.ClassMembershipValidUntil == null || m.ClassMembershipValidUntil > session.TeachingSessionStart)))
                    .Select(session => records.GetValueOrDefault((s.StudentId, session.TeachingSessionId))?.SessionAttendanceStatus ?? "absent"), term.AcademicTermMinimumAttendance) }));
        }).RequireAuthorization(Roles.StaffPolicy);
        group.MapGet("/events", async (Guid? studentId, int? offset, BackendDbContext db, CancellationToken ct) =>
            Results.Ok(await (from e in db.AttendanceEvents.AsNoTracking() join d in db.Set<AttendanceDecision>() on e.AttendanceEventId equals d.AttendanceEventId into decisions
                from decision in decisions.DefaultIfEmpty() where studentId == null || decision.StudentId == studentId
                orderby e.AttendanceReceivedAt descending select new { observation = e, decision }).Skip(Math.Max(0, offset ?? 0)).Take(100).ToListAsync(ct)))
            .RequireAuthorization(Roles.AdministratorPolicy);
    }

    private static IQueryable<Guid> Roster(BackendDbContext db, TeachingSession s) => db.Set<SessionRoster>().Where(r => r.TeachingSessionId == s.TeachingSessionId).Select(r => r.StudentId)
        .Union(db.Set<ClassMembership>().Where(m => m.AcademicClassId == s.AcademicClassId && m.ClassMembershipValidFrom <= s.TeachingSessionStart
            && (m.ClassMembershipValidUntil == null || m.ClassMembershipValidUntil > s.TeachingSessionStart)).Select(m => m.StudentId));
}
public sealed record ManualAttendance(string Status, string Reason, long Revision, DateTimeOffset? OccurredAt = null);
