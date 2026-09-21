using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.Attendance;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.AcademicManagement;

public sealed class SessionService(BackendDbContext db)
{
    public async Task<TeachingSession> Create(AcademicClass academicClass, SessionRequest request, CancellationToken ct)
    {
        var course = await db.Set<Course>().SingleAsync(c => c.CourseId == academicClass.CourseId, ct);
        var session = new TeachingSession
        {
            AcademicClassId = academicClass.AcademicClassId, LecturerId = academicClass.LecturerId,
            TeachingSessionCourseName = course.CourseName, TeachingSessionCourseCredits = course.CourseCredits,
            ReplacesTeachingSessionId = request.ReplacesSessionId
        };
        await Apply(session, request, ct);
        db.Add(session);
        return session;
    }

    public async Task Apply(TeachingSession session, SessionRequest request, CancellationToken ct)
    {
        DomainException.Require(session.TeachingSessionStatus == "scheduled" && !session.TeachingSessionRosterFrozen
            && (session.TeachingSessionStart == default || session.TeachingSessionStart.AddMinutes(-session.TeachingSessionEarlyMinutes) > DateTimeOffset.UtcNow),
            "session_window_already_started", 409);
        DomainException.Require(request.EarlyMinutes is >= 0 and <= 180 && request.LateMinutes is >= 0 and <= 180
            && request.CheckinMinutes >= request.LateMinutes && request.CheckinMinutes <= 1440
            && request.End > request.Start && request.End - request.Start <= TimeSpan.FromHours(12)
            && request.Start.AddMinutes(-request.EarlyMinutes) > DateTimeOffset.UtcNow, "invalid_session_window");
        DomainException.Require(await db.Set<Classroom>().AnyAsync(r => r.ClassroomId == request.ClassroomId && r.ClassroomActive, ct), "invalid_classroom");
        var academicClass = await db.Set<AcademicClass>().SingleAsync(c => c.AcademicClassId == session.AcademicClassId, ct);
        var term = await db.Set<AcademicTerm>().SingleAsync(t => t.AcademicTermId == academicClass.AcademicTermId, ct);
        DomainException.Require(academicClass.AcademicClassActive && term.AcademicTermActive, "class_or_term_inactive", 409);
        var local = TimeZoneInfo.ConvertTime(request.Start, TimeZoneInfo.FindSystemTimeZoneById("Asia/Jakarta"));
        DomainException.Require(DateOnly.FromDateTime(local.DateTime) >= term.AcademicTermStart && DateOnly.FromDateTime(local.DateTime) <= term.AcademicTermEnd,
            "session_outside_term");
        var opens = request.Start.AddMinutes(-request.EarlyMinutes);
        var closes = request.Start.AddMinutes(request.CheckinMinutes) < request.End ? request.Start.AddMinutes(request.CheckinMinutes) : request.End;
        var candidates = await db.Set<TeachingSession>().Where(s => s.TeachingSessionId != session.TeachingSessionId && s.TeachingSessionStatus != "cancelled"
            && s.TeachingSessionStart < request.End.AddHours(3) && s.TeachingSessionEnd > opens
            && (s.ClassroomId == request.ClassroomId || s.LecturerId == session.LecturerId || s.AcademicClassId == session.AcademicClassId)).ToListAsync(ct);
        DomainException.Require(!candidates.Any(s =>
            (s.ClassroomId == request.ClassroomId && s.TeachingSessionStart.AddMinutes(-s.TeachingSessionEarlyMinutes) < closes
                && CloseAt(s) > opens)
            || (s.LecturerId == session.LecturerId || s.AcademicClassId == session.AcademicClassId || s.ClassroomId == request.ClassroomId)
                && s.TeachingSessionStart < request.End && s.TeachingSessionEnd > request.Start), "session_schedule_conflict", 409);
        session.ClassroomId = request.ClassroomId;
        session.TeachingSessionStart = request.Start.ToUniversalTime();
        session.TeachingSessionEnd = request.End.ToUniversalTime();
        session.TeachingSessionEarlyMinutes = request.EarlyMinutes;
        session.TeachingSessionLateMinutes = request.LateMinutes;
        session.TeachingSessionCheckinMinutes = request.CheckinMinutes;
    }

    public async Task FreezeRoster(TeachingSession session, CancellationToken ct)
    {
        if (session.TeachingSessionRosterFrozen) return;
        DomainException.Require(session.TeachingSessionStart <= DateTimeOffset.UtcNow, "session_not_started", 409);
        var ids = await db.Set<ClassMembership>().Where(m => m.AcademicClassId == session.AcademicClassId
            && m.ClassMembershipValidFrom <= session.TeachingSessionStart
            && (m.ClassMembershipValidUntil == null || m.ClassMembershipValidUntil > session.TeachingSessionStart)).Select(m => m.StudentId).Distinct().ToListAsync(ct);
        var existing = await db.Set<SessionRoster>().Where(r => r.TeachingSessionId == session.TeachingSessionId).Select(r => r.StudentId).ToListAsync(ct);
        existing.AddRange(db.Set<SessionRoster>().Local.Where(r => r.TeachingSessionId == session.TeachingSessionId).Select(r => r.StudentId));
        foreach (var id in ids.Except(existing)) db.Add(new SessionRoster { TeachingSessionId = session.TeachingSessionId, StudentId = id });
        session.TeachingSessionRosterFrozen = true;
    }
    public static DateTimeOffset CloseAt(TeachingSession s) => s.TeachingSessionStart.AddMinutes(s.TeachingSessionCheckinMinutes) < s.TeachingSessionEnd
        ? s.TeachingSessionStart.AddMinutes(s.TeachingSessionCheckinMinutes) : s.TeachingSessionEnd;
}
public sealed record SessionRequest(Guid ClassroomId, DateTimeOffset Start, DateTimeOffset End,
    int EarlyMinutes = 15, int LateMinutes = 10, int CheckinMinutes = 60, Guid? ReplacesSessionId = null, long? Revision = null);
