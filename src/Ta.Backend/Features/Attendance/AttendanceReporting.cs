namespace Ta.Backend.Features.Attendance;

public static class AttendanceReporting
{
    public static AttendanceSummary Summarize(Guid classId, IEnumerable<string> attendance, decimal threshold)
    {
        var statuses = attendance.ToArray();
        var present = statuses.Count(s => s == "present");
        var late = statuses.Count(s => s == "late");
        var excused = statuses.Count(s => s == "excused");
        var denominator = statuses.Length - excused;
        decimal? percentage = denominator == 0 ? null : Math.Round(100m * (present + late) / denominator, 2);
        // Compare the exact ratio, not the rounded display percentage at the threshold.
        bool? below = denominator == 0 ? null : 100m * (present + late) < threshold * denominator;
        return new(classId, statuses.Length, present, late, excused, statuses.Count(s => s == "absent"), percentage, threshold, below);
    }
}
public sealed record AttendanceSummary(Guid ClassId, int HeldSessions, int Present, int Late, int Excused, int Absent,
    decimal? Percentage, decimal MinimumPercentage, bool? BelowMinimum);
