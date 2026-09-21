namespace Ta.Backend.Features.Attendance;

public sealed class AttendanceDecision
{
    public Guid AttendanceDecisionId { get; set; } = Guid.NewGuid();
    public Guid AttendanceEventId { get; set; }
    public Guid? StudentId { get; set; }
    public Guid? TeachingSessionId { get; set; }
    public string AttendanceDecisionOutcome { get; set; } = "";
    public DateTimeOffset AttendanceDecisionAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class SessionAttendance
{
    public Guid SessionAttendanceId { get; set; } = Guid.NewGuid();
    public Guid TeachingSessionId { get; set; }
    public Guid StudentId { get; set; }
    public Guid? AttendanceEventId { get; set; }
    public string SessionAttendanceStatus { get; set; } = "absent";
    public string SessionAttendanceSource { get; set; } = "automatic";
    public DateTimeOffset? SessionAttendanceOccurredAt { get; set; }
    public decimal SessionAttendanceLateMinutes { get; set; }
    public long SessionAttendanceRevision { get; set; } = 1;
    public DateTimeOffset SessionAttendanceUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
