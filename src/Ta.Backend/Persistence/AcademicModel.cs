using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ta.Backend.Features.AcademicManagement;
using Ta.Backend.Features.Attendance;
using Ta.Backend.Features.Audit;
using Ta.Backend.Features.Biometrics;
using Ta.Backend.Features.Devices;
using Ta.Backend.Features.Identity;

namespace Ta.Backend.Persistence;

public static class AcademicModel
{
    public static void ConfigureAcademic(this ModelBuilder model)
    {
        Entity<Account>(model, "account", a => a.AccountId);
        Entity<AccountSession>(model, "account_session", a => a.AccountSessionId);
        Entity<StudyProgram>(model, "study_program", a => a.StudyProgramId);
        Entity<AcademicTerm>(model, "academic_term", a => a.AcademicTermId);
        Entity<Course>(model, "course", a => a.CourseId);
        Entity<Classroom>(model, "classroom", a => a.ClassroomId);
        Entity<Student>(model, "student", a => a.StudentId);
        Entity<Lecturer>(model, "lecturer", a => a.LecturerId);
        Entity<AcademicClass>(model, "academic_class", a => a.AcademicClassId);
        Entity<CourseRegistration>(model, "course_registration", a => a.CourseRegistrationId);
        Entity<CourseRegistrationItem>(model, "course_registration_item", a => a.CourseRegistrationItemId);
        Entity<ClassMembership>(model, "class_membership", a => a.ClassMembershipId);
        Entity<AcademicSchedule>(model, "academic_schedule", a => a.AcademicScheduleId);
        Entity<TeachingSession>(model, "teaching_session", a => a.TeachingSessionId);
        Entity<SessionRoster>(model, "session_roster", a => a.SessionRosterId);
        Entity<DeviceRoomAssignment>(model, "device_room_assignment", a => a.DeviceRoomAssignmentId);
        Entity<SessionAttendance>(model, "session_attendance", a => a.SessionAttendanceId);
        Entity<AttendanceDecision>(model, "attendance_decision", a => a.AttendanceDecisionId);
        Entity<AuditRecord>(model, "audit_record", a => a.AuditRecordId);
        Entity<BiometricEnrollment>(model, "biometric_enrollment", a => a.BiometricEnrollmentId);
        Entity<BiometricTemplate>(model, "biometric_template", a => a.BiometricTemplateId);

        Foreign<AccountSession, Account>(model, a => a.AccountId);
        Foreign<Student, Account>(model, a => a.AccountId);
        Foreign<Student, StudyProgram>(model, a => a.StudyProgramId);
        Foreign<Student, Lecturer>(model, a => a.AdvisorLecturerId!);
        Foreign<Lecturer, Account>(model, a => a.AccountId);
        Foreign<Course, StudyProgram>(model, a => a.StudyProgramId);
        Foreign<AcademicClass, AcademicTerm>(model, a => a.AcademicTermId);
        Foreign<AcademicClass, Course>(model, a => a.CourseId);
        Foreign<AcademicClass, Lecturer>(model, a => a.LecturerId);
        Foreign<CourseRegistration, Student>(model, a => a.StudentId);
        Foreign<CourseRegistration, AcademicTerm>(model, a => a.AcademicTermId);
        Foreign<CourseRegistration, Account>(model, a => a.CourseRegistrationReviewerId!);
        Foreign<CourseRegistrationItem, CourseRegistration>(model, a => a.CourseRegistrationId);
        Foreign<CourseRegistrationItem, AcademicClass>(model, a => a.AcademicClassId);
        Foreign<ClassMembership, Student>(model, a => a.StudentId);
        Foreign<ClassMembership, AcademicClass>(model, a => a.AcademicClassId);
        Foreign<ClassMembership, CourseRegistration>(model, a => a.CourseRegistrationId);
        Foreign<AcademicSchedule, AcademicClass>(model, a => a.AcademicClassId);
        Foreign<AcademicSchedule, Classroom>(model, a => a.ClassroomId);
        Foreign<TeachingSession, AcademicClass>(model, a => a.AcademicClassId);
        Foreign<TeachingSession, Lecturer>(model, a => a.LecturerId);
        Foreign<TeachingSession, Classroom>(model, a => a.ClassroomId);
        Foreign<TeachingSession, AcademicSchedule>(model, a => a.AcademicScheduleId!);
        Foreign<TeachingSession, TeachingSession>(model, a => a.ReplacesTeachingSessionId!);
        Foreign<SessionRoster, TeachingSession>(model, a => a.TeachingSessionId);
        Foreign<SessionRoster, Student>(model, a => a.StudentId);
        Foreign<DeviceRoomAssignment, Device>(model, a => a.DeviceId);
        Foreign<DeviceRoomAssignment, Classroom>(model, a => a.ClassroomId);
        Foreign<SessionAttendance, TeachingSession>(model, a => a.TeachingSessionId);
        Foreign<SessionAttendance, Student>(model, a => a.StudentId);
        Foreign<SessionAttendance, AttendanceEvent>(model, a => a.AttendanceEventId!);
        Foreign<AttendanceDecision, AttendanceEvent>(model, a => a.AttendanceEventId);
        Foreign<AttendanceDecision, Student>(model, a => a.StudentId!);
        Foreign<AttendanceDecision, TeachingSession>(model, a => a.TeachingSessionId!);
        Foreign<BiometricEnrollment, Student>(model, a => a.StudentId);
        Foreign<BiometricTemplate, BiometricEnrollment>(model, a => a.BiometricEnrollmentId);

        model.Entity<Account>().HasIndex(a => a.AccountEmail).IsUnique();
        model.Entity<Account>().Property(a => a.AccountPasswordHash).HasMaxLength(1000);
        model.Entity<AccountSession>().HasIndex(a => a.AccountSessionTokenHash).IsUnique();
        model.Entity<StudyProgram>().HasIndex(a => a.StudyProgramCode).IsUnique();
        model.Entity<Course>().HasIndex(a => a.CourseCode).IsUnique();
        model.Entity<Classroom>().HasIndex(a => a.ClassroomCode).IsUnique();
        model.Entity<Student>().HasIndex(a => a.AccountId).IsUnique();
        model.Entity<Student>().HasIndex(a => a.StudentNumber).IsUnique();
        model.Entity<Student>().HasIndex(a => a.StudentIdentityId).IsUnique();
        model.Entity<Lecturer>().HasIndex(a => a.AccountId).IsUnique();
        model.Entity<Lecturer>().HasIndex(a => a.LecturerNumber).IsUnique();
        model.Entity<CourseRegistration>().HasIndex(a => new { a.StudentId, a.AcademicTermId }).IsUnique();
        model.Entity<CourseRegistrationItem>().HasIndex(a => new { a.CourseRegistrationId, a.AcademicClassId }).IsUnique();
        model.Entity<ClassMembership>().HasIndex(a => new { a.StudentId, a.AcademicClassId }).IsUnique()
            .HasFilter("class_membership_valid_until IS NULL");
        model.Entity<DeviceRoomAssignment>().HasIndex(a => a.DeviceId).IsUnique().HasFilter("device_room_assignment_valid_until IS NULL");
        model.Entity<SessionRoster>().HasIndex(a => new { a.TeachingSessionId, a.StudentId }).IsUnique();
        model.Entity<SessionAttendance>().HasIndex(a => new { a.TeachingSessionId, a.StudentId }).IsUnique();
        model.Entity<AttendanceDecision>().HasIndex(a => a.AttendanceEventId).IsUnique();
        model.Entity<TeachingSession>().HasIndex(a => new { a.AcademicClassId, a.TeachingSessionStart }).IsUnique()
            .HasFilter("teaching_session_status <> 'cancelled'");
        model.Entity<TeachingSession>().HasIndex(a => new { a.ClassroomId, a.TeachingSessionStart });
        model.Entity<BiometricEnrollment>().HasIndex(a => a.StudentId).IsUnique().HasFilter("biometric_enrollment_status = 'draft'");
        model.Entity<BiometricTemplate>().HasIndex(a => new { a.BiometricEnrollmentId, a.BiometricTemplateImageSha256 }).IsUnique();
        model.Entity<AuditRecord>().Property(a => a.AuditDetails).HasColumnType("jsonb").Metadata.SetMaxLength(null);
        model.Entity<AuditRecord>().HasIndex(a => a.AuditOccurredAt);
        model.Entity<AuditRecord>().Property(a => a.AuditResource).HasMaxLength(2000);

        Check<Account>(model, "account_role", "account_role IN ('administrator','lecturer','student')");
        Check<Account>(model, "account_status", "account_status IN ('pending','approved','disabled','rejected')");
        Check<AcademicTerm>(model, "term_dates", "academic_term_end >= academic_term_start AND academic_term_minimum_attendance BETWEEN 0 AND 100");
        Check<Course>(model, "course_credits", "course_credits BETWEEN 1 AND 24");
        Check<AcademicClass>(model, "class_capacity", "academic_class_capacity BETWEEN 1 AND 1000");
        Check<CourseRegistration>(model, "registration_status", "course_registration_status IN ('draft','submitted','corrections','approved','withdrawn')");
        Check<AcademicSchedule>(model, "schedule_interval", "academic_schedule_day_of_week BETWEEN 0 AND 6 AND academic_schedule_end > academic_schedule_start AND academic_schedule_valid_until >= academic_schedule_valid_from");
        Check<ClassMembership>(model, "membership_interval", "class_membership_valid_until IS NULL OR class_membership_valid_until >= class_membership_valid_from");
        Check<DeviceRoomAssignment>(model, "device_room_interval", "device_room_assignment_valid_until IS NULL OR device_room_assignment_valid_until >= device_room_assignment_valid_from");
        Check<TeachingSession>(model, "session_interval", "teaching_session_end > teaching_session_start AND teaching_session_early_minutes BETWEEN 0 AND 180 AND teaching_session_late_minutes BETWEEN 0 AND 180 AND teaching_session_checkin_minutes >= teaching_session_late_minutes");
        Check<TeachingSession>(model, "session_status", "teaching_session_status IN ('scheduled','closed','cancelled')");
        Check<SessionAttendance>(model, "attendance_status", "session_attendance_status IN ('present','late','excused','absent') AND session_attendance_source IN ('automatic','manual')");
        Check<BiometricEnrollment>(model, "enrollment_status", "biometric_enrollment_status IN ('draft','submitted','approved','rejected','revoked')");
        Check<BiometricTemplate>(model, "template_size", "octet_length(biometric_template_embedding) = 2048");
        // Apply descriptive snake_case names to newly introduced indexes and constraints.
        foreach (var entity in model.Model.GetEntityTypes().Where(e => e.ClrType != typeof(Device)
            && e.ClrType != typeof(AttendanceEvent) && e.ClrType != typeof(GalleryRelease)))
        {
            foreach (var index in entity.GetIndexes()) index.SetDatabaseName(ShortName("ix_" + entity.GetTableName() + "_" +
                string.Join('_', index.Properties.Select(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name)))));
            foreach (var fk in entity.GetForeignKeys()) fk.SetConstraintName(ShortName("fk_" + entity.GetTableName() + "_" +
                string.Join('_', fk.Properties.Select(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name)))));
        }
    }

    private static string ShortName(string name) => name.Length <= 63 ? name : name[..50] + "_" + Credentials.Hash(name)[..12];

    private static void Entity<T>(ModelBuilder model, string table, Expression<Func<T, object?>> key) where T : class
    {
        var entity = model.Entity<T>();
        entity.ToTable(table);
        entity.HasKey(key).HasName("pk_" + table);
        foreach (var property in typeof(T).GetProperties().Where(p => p.PropertyType == typeof(string)))
            entity.Property(property.Name).HasMaxLength(512).IsRequired();
    }
    private static void Foreign<T, TPrincipal>(ModelBuilder model, Expression<Func<T, object?>> key)
        where T : class where TPrincipal : class => model.Entity<T>().HasOne<TPrincipal>().WithMany()
            .HasForeignKey(key).OnDelete(DeleteBehavior.Restrict);
    private static void Check<T>(ModelBuilder model, string name, string sql) where T : class =>
        model.Entity<T>().ToTable(t => t.HasCheckConstraint("ck_" + name, sql));
}
