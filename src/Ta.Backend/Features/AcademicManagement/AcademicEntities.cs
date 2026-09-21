namespace Ta.Backend.Features.AcademicManagement;

public sealed class StudyProgram
{
    public Guid StudyProgramId { get; set; } = Guid.NewGuid();
    public string StudyProgramCode { get; set; } = "";
    public string StudyProgramName { get; set; } = "";
    public bool StudyProgramActive { get; set; } = true;
}
public sealed class AcademicTerm
{
    public Guid AcademicTermId { get; set; } = Guid.NewGuid();
    public string AcademicTermName { get; set; } = "";
    public DateOnly AcademicTermStart { get; set; }
    public DateOnly AcademicTermEnd { get; set; }
    public decimal AcademicTermMinimumAttendance { get; set; } = 85;
    public bool AcademicTermActive { get; set; } = true;
}
public sealed class Course
{
    public Guid CourseId { get; set; } = Guid.NewGuid();
    public Guid StudyProgramId { get; set; }
    public string CourseCode { get; set; } = "";
    public string CourseName { get; set; } = "";
    public int CourseCredits { get; set; }
    public bool CourseActive { get; set; } = true;
}
public sealed class Classroom
{
    public Guid ClassroomId { get; set; } = Guid.NewGuid();
    public string ClassroomCode { get; set; } = "";
    public string ClassroomName { get; set; } = "";
    public bool ClassroomActive { get; set; } = true;
}
public sealed class Lecturer
{
    public Guid LecturerId { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string LecturerNumber { get; set; } = "";
}
public sealed class Student
{
    public Guid StudentId { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid StudyProgramId { get; set; }
    public Guid? AdvisorLecturerId { get; set; }
    public string StudentNumber { get; set; } = "";
    public string StudentIdentityId { get; set; } = Guid.NewGuid().ToString("D");
    public string StudentPhone { get; set; } = "";
    public int StudentEntryYear { get; set; }
}
public sealed class AcademicClass
{
    public Guid AcademicClassId { get; set; } = Guid.NewGuid();
    public Guid AcademicTermId { get; set; }
    public Guid CourseId { get; set; }
    public Guid LecturerId { get; set; }
    public string AcademicClassName { get; set; } = "";
    public int AcademicClassCapacity { get; set; } = 40;
    public bool AcademicClassActive { get; set; } = true;
}
public sealed class CourseRegistration
{
    public Guid CourseRegistrationId { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public Guid AcademicTermId { get; set; }
    public string CourseRegistrationStatus { get; set; } = "draft";
    public string CourseRegistrationReviewNote { get; set; } = "";
    public DateTimeOffset? CourseRegistrationSubmittedAt { get; set; }
    public DateTimeOffset? CourseRegistrationReviewedAt { get; set; }
    public Guid? CourseRegistrationReviewerId { get; set; }
}
public sealed class CourseRegistrationItem
{
    public Guid CourseRegistrationItemId { get; set; } = Guid.NewGuid();
    public Guid CourseRegistrationId { get; set; }
    public Guid AcademicClassId { get; set; }
}
public sealed class ClassMembership
{
    public Guid ClassMembershipId { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public Guid AcademicClassId { get; set; }
    public Guid CourseRegistrationId { get; set; }
    public DateTimeOffset ClassMembershipValidFrom { get; set; }
    public DateTimeOffset? ClassMembershipValidUntil { get; set; }
}
public sealed class AcademicSchedule
{
    public Guid AcademicScheduleId { get; set; } = Guid.NewGuid();
    public Guid AcademicClassId { get; set; }
    public Guid ClassroomId { get; set; }
    public int AcademicScheduleDayOfWeek { get; set; }
    public TimeOnly AcademicScheduleStart { get; set; }
    public TimeOnly AcademicScheduleEnd { get; set; }
    public DateOnly AcademicScheduleValidFrom { get; set; }
    public DateOnly AcademicScheduleValidUntil { get; set; }
    public string AcademicScheduleTimeZone { get; set; } = "Asia/Jakarta";
}
public sealed class TeachingSession
{
    public Guid TeachingSessionId { get; set; } = Guid.NewGuid();
    public Guid AcademicClassId { get; set; }
    public Guid LecturerId { get; set; }
    public Guid ClassroomId { get; set; }
    public Guid? AcademicScheduleId { get; set; }
    public Guid? ReplacesTeachingSessionId { get; set; }
    public DateTimeOffset TeachingSessionStart { get; set; }
    public DateTimeOffset TeachingSessionEnd { get; set; }
    public int TeachingSessionEarlyMinutes { get; set; } = 15;
    public int TeachingSessionLateMinutes { get; set; } = 10;
    public int TeachingSessionCheckinMinutes { get; set; } = 60;
    public string TeachingSessionStatus { get; set; } = "scheduled";
    public bool TeachingSessionRosterFrozen { get; set; }
    public string TeachingSessionCourseName { get; set; } = "";
    public int TeachingSessionCourseCredits { get; set; }
    public long TeachingSessionRevision { get; set; } = 1;
}
public sealed class SessionRoster
{
    public Guid SessionRosterId { get; set; } = Guid.NewGuid();
    public Guid TeachingSessionId { get; set; }
    public Guid StudentId { get; set; }
}
public sealed class DeviceRoomAssignment
{
    public Guid DeviceRoomAssignmentId { get; set; } = Guid.NewGuid();
    public string DeviceId { get; set; } = "";
    public Guid ClassroomId { get; set; }
    public DateTimeOffset DeviceRoomAssignmentValidFrom { get; set; }
    public DateTimeOffset? DeviceRoomAssignmentValidUntil { get; set; }
}
