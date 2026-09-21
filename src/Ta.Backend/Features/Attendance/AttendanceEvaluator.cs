using Microsoft.EntityFrameworkCore;
using Ta.Backend.Features.AcademicManagement;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Attendance;

public sealed class AttendanceEvaluator(BackendDbContext db)
{
    // Called under the same transaction/academic lock as raw ingestion. Decisions are immutable.
    public async Task Evaluate(Guid eventId, AttendanceObservation observation, CancellationToken ct)
    {
        var decision = new AttendanceDecision { AttendanceEventId = eventId };
        db.Add(decision);
        var student = await db.Set<Student>().SingleOrDefaultAsync(s => s.StudentIdentityId == observation.IdentityId, ct);
        if (student is null) { decision.AttendanceDecisionOutcome = "unknown_identity"; return; }
        decision.StudentId = student.StudentId;
        var at = observation.OccurredAt;
        if (at > DateTimeOffset.UtcNow.AddMinutes(2)) { decision.AttendanceDecisionOutcome = "future_timestamp"; return; }
        var assignment = await db.Set<DeviceRoomAssignment>().SingleOrDefaultAsync(a => a.DeviceId == observation.DeviceId
            && a.DeviceRoomAssignmentValidFrom <= at && (a.DeviceRoomAssignmentValidUntil == null || a.DeviceRoomAssignmentValidUntil > at), ct);
        if (assignment is null) { decision.AttendanceDecisionOutcome = "device_room_unassigned"; return; }
        var candidates = await db.Set<TeachingSession>().Where(s => s.ClassroomId == assignment.ClassroomId
            && s.TeachingSessionStatus != "cancelled" && s.TeachingSessionStart <= at.AddHours(3) && s.TeachingSessionEnd > at).ToListAsync(ct);
        var sessions = candidates.Where(s => s.TeachingSessionStart.AddMinutes(-s.TeachingSessionEarlyMinutes) <= at
            && at < SessionService.CloseAt(s)).ToArray();
        if (sessions.Length != 1) { decision.AttendanceDecisionOutcome = sessions.Length == 0 ? "outside_attendance_window" : "ambiguous_session"; return; }
        var session = sessions[0];
        decision.TeachingSessionId = session.TeachingSessionId;
        // Approval must predate observation AND the session start. Later KRS changes cannot rewrite the past.
        var membershipAt = at < session.TeachingSessionStart ? at : session.TeachingSessionStart;
        if (!await db.Set<ClassMembership>().AnyAsync(m => m.StudentId == student.StudentId && m.AcademicClassId == session.AcademicClassId
            && m.ClassMembershipValidFrom <= membershipAt && (m.ClassMembershipValidUntil == null || m.ClassMembershipValidUntil > at), ct))
        { decision.AttendanceDecisionOutcome = "not_enrolled"; return; }
        if (!await db.Set<SessionRoster>().AnyAsync(r => r.StudentId == student.StudentId && r.TeachingSessionId == session.TeachingSessionId, ct))
            db.Add(new SessionRoster { StudentId = student.StudentId, TeachingSessionId = session.TeachingSessionId });
        var attendance = await db.Set<SessionAttendance>().SingleOrDefaultAsync(a => a.StudentId == student.StudentId && a.TeachingSessionId == session.TeachingSessionId, ct);
        if (attendance is not null && (attendance.SessionAttendanceSource == "manual" || attendance.SessionAttendanceOccurredAt <= at))
        { decision.AttendanceDecisionOutcome = attendance.SessionAttendanceSource == "manual" ? "manual_override_preserved" : "repeated_detection"; return; }
        if (attendance is null)
        {
            attendance = new SessionAttendance { StudentId = student.StudentId, TeachingSessionId = session.TeachingSessionId };
            db.Add(attendance);
        }
        else attendance.SessionAttendanceRevision++;
        attendance.AttendanceEventId = eventId;
        attendance.SessionAttendanceOccurredAt = at;
        attendance.SessionAttendanceLateMinutes = Math.Max(0, (decimal)(at - session.TeachingSessionStart).TotalMinutes);
        attendance.SessionAttendanceStatus = at > session.TeachingSessionStart.AddMinutes(session.TeachingSessionLateMinutes) ? "late" : "present";
        attendance.SessionAttendanceUpdatedAt = DateTimeOffset.UtcNow;
        decision.AttendanceDecisionOutcome = attendance.SessionAttendanceStatus;
    }
}
