using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ta.Backend.Common;
using Ta.Backend.Features.AcademicManagement;
using Ta.Backend.Features.Attendance;
using Ta.Backend.Features.Audit;
using Ta.Backend.Features.Biometrics;
using Ta.Backend.Features.Identity;

namespace Ta.Backend.Tests;

public sealed class AcademicWorkflowTests(BackendFixture fixture) : IClassFixture<BackendFixture>, IDisposable
{
    // Share the isolated PostgreSQL schema, but give each test its own host and
    // rate-limit window so unrelated scenarios cannot consume one another's quota.
    private readonly WebApplicationFactory<Program> application = fixture.WithWebHostBuilder(_ => { });
    private const string Password = "Research-prototype-123!";
    private const string V1 = "/api/v1";
    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {content}");
        return string.IsNullOrWhiteSpace(content) ? default : JsonDocument.Parse(content).RootElement.Clone();
    }
    private static Task<HttpResponseMessage> Post(HttpClient c, string path, object body) => c.PostAsJsonAsync(V1 + path, body, WireJson.Options);
    private static Task<HttpResponseMessage> Put(HttpClient c, string path, object body) => c.PutAsJsonAsync(V1 + path, body, WireJson.Options);
    private HttpClient Client(string? token = null)
    {
        var client = application.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
    public void Dispose() => application.Dispose();
    private async Task<HttpClient> Login(string email)
    {
        using var anonymous = Client();
        var login = await Ok(await Post(anonymous, "/auth/login", new { email, password = Password }));
        return Client(login.GetProperty("access_token").GetString());
    }
    private async Task<Scenario> Setup(bool approveKrs = true)
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var admin = Client(fixture.AdminToken);
        var program = await Ok(await Post(admin, "/academic/study-programs", new StudyProgram { StudyProgramCode = suffix, StudyProgramName = "Lab" }));
        var programId = program.GetProperty("study_program_id").GetGuid();
        var term = await Ok(await Post(admin, "/academic/terms", new AcademicTerm { AcademicTermName = suffix,
            AcademicTermStart = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)), AcademicTermEnd = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(60)) }));
        var termId = term.GetProperty("academic_term_id").GetGuid();
        var email = suffix + "@student.example";
        using var anonymous = Client();
        var registration = await Ok(await Post(anonymous, "/auth/register", new RegisterStudent(email, Password, "Student", suffix, programId, 2026)));
        var accountId = registration.GetProperty("account_id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(anonymous, "/auth/login", new { email, password = Password })).StatusCode);
        await Ok(await Put(admin, $"/admin/accounts/{accountId}/status", new AccountStatus("approved", "Registration verified")));
        var lecturerEmail = suffix + "@lecturer.example";
        var staff = await Ok(await Post(admin, "/admin/accounts", new CreateStaff(lecturerEmail, Password, "Lecturer", Roles.Lecturer, suffix)));
        var lecturerAccount = staff.GetProperty("account_id").GetGuid();
        await using var db = fixture.OpenDatabase();
        var lecturer = await db.Set<Lecturer>().SingleAsync(l => l.AccountId == lecturerAccount);
        var student = await db.Set<Student>().SingleAsync(s => s.AccountId == accountId);
        await Ok(await Put(admin, $"/academic/students/{student.StudentId}", new StudentAdministration(lecturer.LecturerId)));
        var course = await Ok(await Post(admin, "/academic/courses", new Course { StudyProgramId = programId, CourseCode = suffix, CourseName = "Research", CourseCredits = 3 }));
        var classroom = await Ok(await Post(admin, "/academic/classrooms", new Classroom { ClassroomCode = suffix, ClassroomName = "Lab" }));
        var roomId = classroom.GetProperty("classroom_id").GetGuid();
        var academicClass = await Ok(await Post(admin, "/academic/classes", new AcademicClass { CourseId = course.GetProperty("course_id").GetGuid(), AcademicTermId = termId,
            LecturerId = lecturer.LecturerId, AcademicClassName = suffix, AcademicClassCapacity = 1 }));
        var classId = academicClass.GetProperty("academic_class_id").GetGuid();
        var studentClient = await Login(email);
        var lecturerClient = await Login(lecturerEmail);
        var krs = await Ok(await Post(studentClient, "/course-registrations", new SaveRegistration(termId, [classId])));
        var krsId = krs.GetProperty("course_registration_id").GetGuid();
        await Ok(await Post(studentClient, $"/course-registrations/{krsId}/submit", new { }));
        if (approveKrs) await Ok(await Post(lecturerClient, $"/course-registrations/{krsId}/review", new RegistrationReview("approved", "Approved by advisor")));
        var deviceId = "academic-" + suffix;
        var device = await Ok(await Post(admin, "/admin/devices", new { device_id = deviceId, device_name = "Lab edge" }));
        await Ok(await Put(admin, $"/admin/devices/{deviceId}/classroom", new { classroom_id = roomId }));
        return new(studentClient, lecturerClient, Client(device.GetProperty("token").GetString()), student, lecturer, accountId, termId, classId, roomId, krsId, deviceId, email);
    }

    [Fact]
    public async Task Advisees_are_visible_only_to_the_assigned_lecturer()
    {
        var first = await Setup();
        var second = await Setup();
        var rows = await Ok(await first.LecturerClient.GetAsync(V1 + "/academic/advisees"));
        var ids = rows.EnumerateArray().Select(r => r.GetProperty("student").GetProperty("student_id").GetGuid()).ToArray();
        Assert.Contains(first.Student.StudentId, ids);
        Assert.DoesNotContain(second.Student.StudentId, ids);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.StudentClient.GetAsync(V1 + "/academic/advisees")).StatusCode);
        using var anonymous = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(V1 + "/academic/advisees")).StatusCode);
    }

    [Fact]
    public async Task Session_creation_accepts_WIB_offsets_and_persists_the_same_instant_in_UTC()
    {
        var scenario = await Setup();
        var start = DateTimeOffset.UtcNow.AddDays(3).ToOffset(TimeSpan.FromHours(7));
        var response = await Ok(await Post(scenario.LecturerClient, $"/teaching/classes/{scenario.ClassId}/sessions",
            new SessionRequest(scenario.RoomId, start, start.AddHours(2))));
        Assert.Equal(start, response.GetProperty("teaching_session_start").GetDateTimeOffset());
        Assert.Equal(TimeSpan.Zero, response.GetProperty("teaching_session_start").GetDateTimeOffset().Offset);
        var from = Uri.EscapeDataString(start.AddMinutes(-1).ToString("O"));
        var until = Uri.EscapeDataString(start.AddHours(3).ToString("O"));
        var list = await Ok(await scenario.LecturerClient.GetAsync(V1 + $"/teaching/sessions?from={from}&until={until}"));
        Assert.Contains(list.EnumerateArray(), item => item.GetProperty("teaching_session_id").GetGuid() == response.GetProperty("teaching_session_id").GetGuid());
    }

    [Fact]
    public async Task Audit_filters_paginate_after_filtering_and_preserve_administrator_access()
    {
        using var s = await Setup();
        using var admin = Client(fixture.AdminToken);
        using var anonymous = Client();
        var resource = Guid.NewGuid().ToString();
        var at = new DateTimeOffset(2026, 6, 1, 3, 0, 0, TimeSpan.Zero);
        await using (var db = fixture.OpenDatabase())
        {
            for (var i = 0; i < 101; i++) db.Add(new AuditRecord { AuditActor = s.Student.AccountId.ToString(),
                AuditAction = "attendance.correct", AuditResource = resource, AuditOccurredAt = at.AddSeconds(i),
                AuditDetails = "{\"reason\":\"Integration audit fixture\"}" });
            db.Add(new AuditRecord { AuditAction = "account_status", AuditResource = resource, AuditOccurredAt = at });
            await db.SaveChangesAsync();
        }
        var from = Uri.EscapeDataString(at.ToOffset(TimeSpan.FromHours(7)).ToString("O"));
        var until = Uri.EscapeDataString(at.AddMinutes(2).ToOffset(TimeSpan.FromHours(7)).ToString("O"));
        var path = V1 + $"/admin/audit-records?action=attendance.correct&resource={resource}&actor={s.Student.AccountId}&from={from}&until={until}";
        var first = await Ok(await admin.GetAsync(path));
        var second = await Ok(await admin.GetAsync(path + "&offset=100"));
        Assert.Equal(100, first.GetArrayLength());
        Assert.Single(second.EnumerateArray());
        Assert.Equal("Student", first[0].GetProperty("actor_name").GetString());
        Assert.Equal(at.AddSeconds(100), first[0].GetProperty("audit_occurred_at").GetDateTimeOffset());
        Assert.DoesNotContain(first.EnumerateArray(), a => a.GetProperty("audit_record_id").GetGuid() == second[0].GetProperty("audit_record_id").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(V1 + $"/admin/audit-records?from={until}&until={from}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.StudentClient.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.LecturerClient.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Ongoing_attendance_shows_confirmed_records_without_entering_completed_percentage()
    {
        using var s = await Setup();
        using var other = await Setup();
        _ = await HistoricalSession(s);
        var ongoing = new TeachingSession { AcademicClassId = s.ClassId, LecturerId = s.Lecturer.LecturerId,
            ClassroomId = s.RoomId, TeachingSessionStart = DateTimeOffset.UtcNow.AddMinutes(-10),
            TeachingSessionEnd = DateTimeOffset.UtcNow.AddMinutes(50), TeachingSessionCourseName = "Ongoing fixture" };
        await using (var db = fixture.OpenDatabase()) { db.Add(ongoing); await db.SaveChangesAsync(); }
        var path = V1 + $"/attendance/students/{s.Student.StudentId}";
        var before = await Ok(await s.StudentClient.GetAsync(path));
        Assert.Single(before.GetProperty("ongoing_sessions").EnumerateArray());
        Assert.Equal("pending", before.GetProperty("ongoing_sessions")[0].GetProperty("status").GetString());
        Assert.Equal(1, before.GetProperty("summaries")[0].GetProperty("held_sessions").GetInt32());
        await Ok(await Put(s.LecturerClient, $"/attendance/sessions/{ongoing.TeachingSessionId}/students/{s.Student.StudentId}",
            new ManualAttendance("present", "Verified during class", 0)));
        var after = await Ok(await s.StudentClient.GetAsync(path));
        Assert.Equal("present", after.GetProperty("ongoing_sessions")[0].GetProperty("status").GetString());
        Assert.Equal(0, after.GetProperty("summaries")[0].GetProperty("percentage").GetDecimal());
        Assert.Equal(HttpStatusCode.Forbidden, (await other.StudentClient.GetAsync(path)).StatusCode);
        await using (var db = fixture.OpenDatabase())
        {
            var row = await db.Set<TeachingSession>().SingleAsync(x => x.TeachingSessionId == ongoing.TeachingSessionId);
            row.TeachingSessionStatus = "cancelled";
            await db.SaveChangesAsync();
        }
        var cancelled = await Ok(await s.StudentClient.GetAsync(path));
        Assert.Empty(cancelled.GetProperty("ongoing_sessions").EnumerateArray());
    }

    [Fact]
    public async Task Report_sources_reject_students_and_unassigned_lecturers()
    {
        using var s = await Setup();
        using var other = await Setup();
        using var anonymous = Client();
        using var admin = Client(fixture.AdminToken);
        var session = await HistoricalSession(s);
        foreach (var path in new[] { $"/attendance/sessions/{session.TeachingSessionId}", $"/attendance/classes/{s.ClassId}/summary" })
        {
            await Ok(await s.LecturerClient.GetAsync(V1 + path));
            await Ok(await admin.GetAsync(V1 + path));
            Assert.Equal(HttpStatusCode.Forbidden, (await s.StudentClient.GetAsync(V1 + path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.LecturerClient.GetAsync(V1 + path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(V1 + path)).StatusCode);
        }
    }

    [Fact]
    public async Task Enrollment_status_is_own_only_and_reports_compatible_active_samples_without_embeddings()
    {
        using var s = await Setup();
        using var other = await Setup();
        using var anonymous = Client();
        var endpoint = V1 + "/biometric-enrollments/my-status";
        var empty = await Ok(await s.StudentClient.GetAsync(endpoint));
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("latest_enrollment").ValueKind);
        var approved = new BiometricEnrollment { StudentId = s.Student.StudentId, BiometricEnrollmentStatus = "approved", BiometricEnrollmentCreatedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        var embedding = new byte[2048];
        BitConverter.GetBytes(1f).CopyTo(embedding, 0);
        await using (var db = fixture.OpenDatabase())
        {
            db.Add(approved);
            db.Add(new BiometricTemplate { BiometricEnrollmentId = approved.BiometricEnrollmentId, BiometricTemplateActive = true,
                BiometricTemplatePose = "frontal", BiometricTemplateModelSha256 = fixture.ModelHash, BiometricTemplateImageSha256 = new string('1', 64), BiometricTemplateEmbedding = embedding });
            db.Add(new BiometricTemplate { BiometricEnrollmentId = approved.BiometricEnrollmentId, BiometricTemplateActive = true,
                BiometricTemplatePose = "left", BiometricTemplateModelSha256 = new string('f', 64), BiometricTemplateImageSha256 = new string('2', 64), BiometricTemplateEmbedding = embedding });
            await db.SaveChangesAsync();
        }
        await Ok(await Post(s.StudentClient, "/biometric-enrollments", new EnrollmentConsent(true, "research-v1")));
        var status = await Ok(await s.StudentClient.GetAsync(endpoint));
        Assert.Equal("draft", status.GetProperty("latest_enrollment").GetProperty("biometric_enrollment_status").GetString());
        Assert.Equal(0, status.GetProperty("latest_sample_count").GetInt32());
        Assert.Equal(1, status.GetProperty("active_sample_count").GetInt32());
        Assert.DoesNotContain("embedding", status.GetRawText());
        var otherStatus = await Ok(await other.StudentClient.GetAsync(endpoint));
        Assert.Equal(0, otherStatus.GetProperty("active_sample_count").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await s.LecturerClient.GetAsync(endpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(endpoint)).StatusCode);
    }

    // Controlled historical fixtures avoid sleeping or adding backdating capabilities to production APIs.
    private async Task<TeachingSession> HistoricalSession(Scenario s)
    {
        var response = await Ok(await Post(s.LecturerClient, $"/teaching/classes/{s.ClassId}/sessions", new SessionRequest(s.RoomId, DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(2).AddHours(2))));
        var id = response.GetProperty("teaching_session_id").GetGuid();
        await using var db = fixture.OpenDatabase();
        var session = await db.Set<TeachingSession>().SingleAsync(x => x.TeachingSessionId == id);
        session.TeachingSessionStart = DateTimeOffset.UtcNow.AddDays(-1);
        session.TeachingSessionEnd = session.TeachingSessionStart.AddHours(2);
        foreach (var m in await db.Set<ClassMembership>().Where(m => m.StudentId == s.Student.StudentId).ToListAsync()) m.ClassMembershipValidFrom = session.TeachingSessionStart.AddDays(-1);
        var assignment = await db.Set<DeviceRoomAssignment>().SingleAsync(a => a.DeviceId == s.DeviceId);
        assignment.DeviceRoomAssignmentValidFrom = session.TeachingSessionStart.AddDays(-1);
        await db.SaveChangesAsync();
        return session;
    }
    private static AttendanceObservation Observation(Scenario s, DateTimeOffset at) => new(1, Guid.CreateVersion7().ToString(), at,
        s.DeviceId, s.Student.StudentIdentityId, "offline-gallery", "edge-v1", "1", "1", .8, .15, .99);
    private static Task<HttpResponseMessage> Send(Scenario s, params AttendanceObservation[] events) =>
        Post(s.EdgeClient, "/attendance-events/batch", new { schema_version = 1, device_id = s.DeviceId, events });

    [Fact]
    public async Task Human_accounts_restrict_roles_revoke_sessions_and_hash_credentials()
    {
        using var s = await Setup();
        Assert.Equal(HttpStatusCode.Forbidden, (await s.StudentClient.GetAsync(V1 + "/admin/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.EdgeClient.GetAsync(V1 + "/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.StudentClient.GetAsync(V1 + "/gallery")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(s.StudentClient, $"/course-registrations/{s.KrsId}/review", new RegistrationReview("approved", "spoof"))).StatusCode);
        var secret = s.StudentClient.DefaultRequestHeaders.Authorization!.Parameter!;
        await using var db = fixture.OpenDatabase();
        var account = await db.Set<Account>().SingleAsync(a => a.AccountId == s.AccountId);
        Assert.NotEqual(Password, account.AccountPasswordHash);
        Assert.False(await db.Set<AccountSession>().AnyAsync(x => x.AccountSessionTokenHash == secret));
        await Ok(await Post(s.StudentClient, "/auth/logout", new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.StudentClient.GetAsync(V1 + "/auth/me")).StatusCode);
        using var login = await Login(s.Email);
        using var admin = Client(fixture.AdminToken);
        await Ok(await Put(admin, $"/admin/accounts/{s.AccountId}/status", new AccountStatus("disabled", "Access revoked")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await login.GetAsync(V1 + "/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Krs_corrections_and_advisor_scope_preserve_approval_time()
    {
        using var s = await Setup(false);
        using var other = await Setup(false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(other.LecturerClient, $"/course-registrations/{s.KrsId}/review", new RegistrationReview("approved", "wrong advisor"))).StatusCode);
        await Ok(await Post(s.LecturerClient, $"/course-registrations/{s.KrsId}/review", new RegistrationReview("corrections", "Review selection")));
        await Ok(await Post(s.StudentClient, "/course-registrations", new SaveRegistration(s.TermId, [s.ClassId])));
        await Ok(await Post(s.StudentClient, $"/course-registrations/{s.KrsId}/submit", new { }));
        var before = DateTimeOffset.UtcNow;
        await Ok(await Post(s.LecturerClient, $"/course-registrations/{s.KrsId}/review", new RegistrationReview("approved", "Corrected")));
        Assert.Equal(HttpStatusCode.Conflict, (await Post(s.StudentClient, "/course-registrations", new SaveRegistration(s.TermId, [s.ClassId]))).StatusCode);
        await using var db = fixture.OpenDatabase();
        var membership = await db.Set<ClassMembership>().SingleAsync(m => m.StudentId == s.Student.StudentId);
        Assert.True(membership.ClassMembershipValidFrom >= before);
    }

    [Fact]
    public async Task Class_capacity_conflict_rolls_back_krs_approval()
    {
        using var s = await Setup();
        var unique = Guid.NewGuid().ToString("N");
        var email = unique + "@student.example";
        using var anonymous = Client();
        using var admin = Client(fixture.AdminToken);
        var account = await Ok(await Post(anonymous, "/auth/register", new RegisterStudent(email, Password, "Second student", unique, s.Student.StudyProgramId, 2026)));
        var accountId = account.GetProperty("account_id").GetGuid();
        await Ok(await Put(admin, $"/admin/accounts/{accountId}/status", new AccountStatus("approved", "Verified")));
        await using var db = fixture.OpenDatabase();
        var second = await db.Set<Student>().SingleAsync(x => x.AccountId == accountId);
        await Ok(await Put(admin, $"/academic/students/{second.StudentId}", new StudentAdministration(s.Lecturer.LecturerId)));
        using var student = await Login(email);
        var draft = await Ok(await Post(student, "/course-registrations", new SaveRegistration(s.TermId, [s.ClassId])));
        var id = draft.GetProperty("course_registration_id").GetGuid();
        await Ok(await Post(student, $"/course-registrations/{id}/submit", new { }));
        Assert.Equal(HttpStatusCode.Conflict, (await Post(s.LecturerClient, $"/course-registrations/{id}/review", new RegistrationReview("approved", "Reviewed"))).StatusCode);
        Assert.Equal("submitted", (await db.Set<CourseRegistration>().SingleAsync(x => x.CourseRegistrationId == id)).CourseRegistrationStatus);
        Assert.False(await db.Set<ClassMembership>().AnyAsync(x => x.StudentId == second.StudentId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Krs_approval_revalidates_program_and_course_after_submission(bool programChanged)
    {
        using var s = await Setup(false);
        using var admin = Client(fixture.AdminToken);
        await using var db = fixture.OpenDatabase();
        if (programChanged)
        {
            var program = await Ok(await Post(admin, "/academic/study-programs", new StudyProgram
                { StudyProgramCode = Guid.NewGuid().ToString("N"), StudyProgramName = "Transferred program" }));
            await Ok(await Put(admin, $"/academic/students/{s.Student.StudentId}", new StudentAdministration(s.Lecturer.LecturerId,
                StudyProgramId: program.GetProperty("study_program_id").GetGuid())));
        }
        else
        {
            var courseId = await db.Set<AcademicClass>().Where(c => c.AcademicClassId == s.ClassId).Select(c => c.CourseId).SingleAsync();
            var course = await db.Set<Course>().SingleAsync(c => c.CourseId == courseId);
            course.CourseActive = false;
            await Ok(await Put(admin, $"/academic/courses/{courseId}", course));
        }
        var response = await Post(s.LecturerClient, $"/course-registrations/{s.KrsId}/review", new RegistrationReview("approved", "Reviewed"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("course_selection_no_longer_valid", await response.Content.ReadAsStringAsync());
        Assert.False(await db.Set<ClassMembership>().AnyAsync(m => m.StudentId == s.Student.StudentId));
        Assert.Equal("submitted", (await db.Set<CourseRegistration>().SingleAsync(r => r.CourseRegistrationId == s.KrsId)).CourseRegistrationStatus);
    }

    [Fact]
    public async Task Human_administrator_can_manage_catalogs_and_failed_logins_lock_account()
    {
        var unique = Guid.NewGuid().ToString("N");
        var email = unique + "@admin.example";
        using var bootstrap = Client(fixture.AdminToken);
        await Ok(await Post(bootstrap, "/admin/accounts", new CreateStaff(email, Password, "Administrator", Roles.Administrator, null)));
        using var admin = await Login(email);
        await Ok(await Post(admin, "/academic/classrooms", new Classroom { ClassroomCode = unique, ClassroomName = "Authorized" }));
        using var anonymous = Client();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, "/auth/login", new { email, password = "Wrong-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, "/auth/login", new { email, password = Password })).StatusCode);
    }

    [Fact]
    public async Task Offline_events_use_historical_room_and_membership_and_earliest_occurrence()
    {
        using var s = await Setup();
        var session = await HistoricalSession(s);
        using var admin = Client(fixture.AdminToken);
        await Ok(await Put(admin, $"/admin/devices/{s.DeviceId}/classroom", new { classroom_id = (Guid?)null }));
        await Ok(await Post(s.LecturerClient, $"/course-registrations/{s.KrsId}/withdraw", new ReviewNote("Changed registration")));
        var late = Observation(s, session.TeachingSessionStart.AddMinutes(20));
        var early = Observation(s, session.TeachingSessionStart.AddMinutes(-5));
        await Ok(await Send(s, late));
        await Ok(await Send(s, early));
        var replay = await Ok(await Send(s, early));
        Assert.Equal("duplicate", replay.GetProperty("results")[0].GetProperty("status").GetString());
        await Ok(await Send(s, Observation(s, session.TeachingSessionStart.AddMinutes(30))));
        await using var db = fixture.OpenDatabase();
        var record = await db.Set<SessionAttendance>().SingleAsync(a => a.TeachingSessionId == session.TeachingSessionId);
        Assert.Equal("present", record.SessionAttendanceStatus);
        Assert.Equal(Guid.Parse(early.EventId), record.AttendanceEventId);
        Assert.Equal(3, await db.Set<AttendanceDecision>().CountAsync(d => d.StudentId == s.Student.StudentId));
        Assert.True(await db.Set<AttendanceDecision>().AnyAsync(d => d.StudentId == s.Student.StudentId && d.AttendanceDecisionOutcome == "repeated_detection"));
        var report = await Ok(await s.StudentClient.GetAsync(V1 + $"/attendance/students/{s.Student.StudentId}"));
        Assert.Equal(100m, report.GetProperty("summaries")[0].GetProperty("percentage").GetDecimal());
    }

    [Fact]
    public async Task Ineligible_events_are_durable_without_becoming_attendance()
    {
        using var s = await Setup(false);
        var session = await HistoricalSession(s);
        var observation = Observation(s, session.TeachingSessionStart.AddMinutes(5));
        var response = await Ok(await Send(s, observation));
        Assert.Equal("accepted", response.GetProperty("results")[0].GetProperty("status").GetString());
        await Ok(await Post(s.LecturerClient, $"/course-registrations/{s.KrsId}/review", new RegistrationReview("approved", "Late approval")));
        await Ok(await Send(s, Observation(s, session.TeachingSessionStart.AddMinutes(6))));
        await Ok(await Send(s, Observation(s, DateTimeOffset.UtcNow.AddMinutes(5))));
        await using var db = fixture.OpenDatabase();
        Assert.False(await db.Set<SessionAttendance>().AnyAsync(a => a.TeachingSessionId == session.TeachingSessionId));
        Assert.Equal(2, await db.Set<AttendanceDecision>().CountAsync(d => d.StudentId == s.Student.StudentId && d.AttendanceDecisionOutcome == "not_enrolled"));
        Assert.True(await db.Set<AttendanceDecision>().AnyAsync(d => d.StudentId == s.Student.StudentId && d.AttendanceDecisionOutcome == "future_timestamp"));
        Assert.Equal(3, await db.AttendanceEvents.CountAsync(e => e.DeviceId == s.DeviceId));
    }

    [Fact]
    public async Task Manual_corrections_are_scoped_audited_revision_checked_and_survive_offline_events()
    {
        using var s = await Setup();
        using var other = await Setup();
        var session = await HistoricalSession(s);
        var path = $"/attendance/sessions/{session.TeachingSessionId}/students/{s.Student.StudentId}";
        var correction = new ManualAttendance("excused", "Medical permission reviewed", 0);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(other.LecturerClient, path, correction)).StatusCode);
        await Ok(await Put(s.LecturerClient, path, correction));
        Assert.Equal(HttpStatusCode.Conflict, (await Put(s.LecturerClient, path, correction)).StatusCode);
        await Ok(await Send(s, Observation(s, session.TeachingSessionStart.AddMinutes(5))));
        await using var db = fixture.OpenDatabase();
        var record = await db.Set<SessionAttendance>().SingleAsync(a => a.TeachingSessionId == session.TeachingSessionId);
        Assert.Equal("manual", record.SessionAttendanceSource);
        Assert.Equal("excused", record.SessionAttendanceStatus);
        var audit = await db.Set<AuditRecord>().SingleAsync(a => a.AuditAction == "attendance.correct" && a.AuditResource == record.SessionAttendanceId.ToString());
        Assert.Contains("Medical permission reviewed", audit.AuditDetails);
        var report = await Ok(await s.StudentClient.GetAsync(V1 + $"/attendance/students/{s.Student.StudentId}"));
        Assert.Equal(JsonValueKind.Null, report.GetProperty("summaries")[0].GetProperty("percentage").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.StudentClient.GetAsync(V1 + $"/attendance/students/{s.Student.StudentId}")).StatusCode);
    }

    [Fact]
    public async Task Manual_correction_accepts_WIB_checkin_time_without_changing_the_instant()
    {
        using var scenario = await Setup();
        var session = await HistoricalSession(scenario);
        var occurredAt = session.TeachingSessionStart.AddMinutes(5).ToOffset(TimeSpan.FromHours(7));
        var path = $"/attendance/sessions/{session.TeachingSessionId}/students/{scenario.Student.StudentId}";
        await Ok(await Put(scenario.LecturerClient, path, new ManualAttendance("present", "Verified check-in time", 0, occurredAt)));
        await using var db = fixture.OpenDatabase();
        var saved = await db.Set<SessionAttendance>().SingleAsync(a => a.TeachingSessionId == session.TeachingSessionId);
        // PostgreSQL persists timestamps at microsecond precision.
        Assert.InRange(Math.Abs((saved.SessionAttendanceOccurredAt!.Value - occurredAt).Ticks), 0, 9);
        Assert.Equal(TimeSpan.Zero, saved.SessionAttendanceOccurredAt.Value.Offset);
    }

    [Fact]
    public async Task Sessions_detect_conflicts_support_replacements_and_freeze_started_rules()
    {
        using var s = await Setup();
        var start = DateTimeOffset.UtcNow.AddDays(3);
        var request = new SessionRequest(s.RoomId, start, start.AddHours(2));
        var created = await Ok(await Post(s.LecturerClient, $"/teaching/classes/{s.ClassId}/sessions", request));
        var id = created.GetProperty("teaching_session_id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await Post(s.LecturerClient, $"/teaching/classes/{s.ClassId}/sessions", request)).StatusCode);
        await Ok(await Post(s.LecturerClient, $"/teaching/sessions/{id}/cancel", new ReviewNote("Rescheduled")));
        var replacement = await Ok(await Post(s.LecturerClient, $"/teaching/classes/{s.ClassId}/sessions", request with { Start = start.AddDays(1), End = start.AddDays(1).AddHours(2), ReplacesSessionId = id }));
        Assert.Equal(id, replacement.GetProperty("replaces_teaching_session_id").GetGuid());
        var historical = await HistoricalSession(s);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(s.LecturerClient, $"/teaching/sessions/{historical.TeachingSessionId}", request with { Revision = 1 })).StatusCode);
    }

    [Fact]
    public async Task Recurring_schedule_generation_is_idempotent_and_temporary_room_changes_do_not_rewrite_it()
    {
        using var s = await Setup();
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(8));
        var until = from.AddDays(14);
        var schedule = await Ok(await Post(s.LecturerClient, $"/teaching/classes/{s.ClassId}/schedules",
            new ScheduleRequest(s.RoomId, (int)from.DayOfWeek, new TimeOnly(9, 0), new TimeOnly(11, 0), from, until)));
        var scheduleId = schedule.GetProperty("academic_schedule_id").GetGuid();
        var generated = await Ok(await Post(s.LecturerClient, $"/teaching/schedules/{scheduleId}/generate", new GenerateSessions(from, until)));
        var ids = generated.GetProperty("teaching_session_ids").EnumerateArray().Select(x => x.GetGuid()).ToArray();
        Assert.Equal(3, ids.Length);
        var replay = await Ok(await Post(s.LecturerClient, $"/teaching/schedules/{scheduleId}/generate", new GenerateSessions(from, until)));
        Assert.Equal(ids, replay.GetProperty("teaching_session_ids").EnumerateArray().Select(x => x.GetGuid()));
        using var admin = Client(fixture.AdminToken);
        var room = await Ok(await Post(admin, "/academic/classrooms", new Classroom { ClassroomCode = Guid.NewGuid().ToString(), ClassroomName = "Replacement room" }));
        var roomId = room.GetProperty("classroom_id").GetGuid();
        await using var db = fixture.OpenDatabase();
        var session = await db.Set<TeachingSession>().AsNoTracking().SingleAsync(x => x.TeachingSessionId == ids[0]);
        await Ok(await Put(s.LecturerClient, $"/teaching/sessions/{session.TeachingSessionId}", new SessionRequest(roomId, session.TeachingSessionStart, session.TeachingSessionEnd, Revision: 1)));
        Assert.Equal(s.RoomId, (await db.Set<AcademicSchedule>().SingleAsync(x => x.AcademicScheduleId == scheduleId)).ClassroomId);
        Assert.Equal(roomId, (await db.Set<TeachingSession>().SingleAsync(x => x.TeachingSessionId == ids[0])).ClassroomId);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(s.LecturerClient, $"/teaching/classes/{s.ClassId}/schedules",
            new ScheduleRequest(roomId, (int)from.DayOfWeek, new TimeOnly(9, 0), new TimeOnly(11, 0), from.AddDays(7), until, scheduleId))).StatusCode);
        foreach (var oldId in ids.Skip(1)) await Ok(await Post(s.LecturerClient, $"/teaching/sessions/{oldId}/cancel", new ReviewNote("Permanent room change")));
        var replacement = await Ok(await Post(s.LecturerClient, $"/teaching/classes/{s.ClassId}/schedules",
            new ScheduleRequest(roomId, (int)from.DayOfWeek, new TimeOnly(9, 0), new TimeOnly(11, 0), from.AddDays(7), until, scheduleId)));
        var replacementId = replacement.GetProperty("academic_schedule_id").GetGuid();
        var newSessions = await Ok(await Post(s.LecturerClient, $"/teaching/schedules/{replacementId}/generate", new GenerateSessions(from.AddDays(7), until)));
        var newIds = newSessions.GetProperty("teaching_session_ids").EnumerateArray().Select(x => x.GetGuid()).ToArray();
        Assert.Equal(2, newIds.Length);
        Assert.DoesNotContain(newIds, id => ids.Contains(id));
        Assert.Equal(2, await db.Set<TeachingSession>().CountAsync(x => newIds.Contains(x.TeachingSessionId) && x.ClassroomId == roomId && x.TeachingSessionStatus == "scheduled"));
    }

    [Fact]
    public async Task Reports_count_actual_held_sessions_and_lateness_without_hour_conversion()
    {
        using var s = await Setup();
        var first = await HistoricalSession(s);
        await using (var db = fixture.OpenDatabase())
        {
            db.Add(new TeachingSession { AcademicClassId = s.ClassId, ClassroomId = s.RoomId, LecturerId = s.Lecturer.LecturerId,
                TeachingSessionStart = first.TeachingSessionStart.AddHours(3), TeachingSessionEnd = first.TeachingSessionEnd.AddHours(3),
                TeachingSessionCourseName = "Research", TeachingSessionCourseCredits = 3 });
            db.Add(new TeachingSession { AcademicClassId = s.ClassId, ClassroomId = s.RoomId, LecturerId = s.Lecturer.LecturerId,
                TeachingSessionStart = first.TeachingSessionStart.AddHours(6), TeachingSessionEnd = first.TeachingSessionEnd.AddHours(6),
                TeachingSessionStatus = "cancelled", TeachingSessionCourseName = "Research", TeachingSessionCourseCredits = 3 });
            await db.SaveChangesAsync();
        }
        await Ok(await Send(s, Observation(s, first.TeachingSessionStart.AddMinutes(20))));
        var report = await Ok(await s.StudentClient.GetAsync(V1 + $"/attendance/students/{s.Student.StudentId}"));
        var summary = report.GetProperty("summaries")[0];
        Assert.Equal(2, summary.GetProperty("held_sessions").GetInt32());
        Assert.Equal(1, summary.GetProperty("late").GetInt32());
        Assert.Equal(1, summary.GetProperty("absent").GetInt32());
        Assert.Equal(50, summary.GetProperty("percentage").GetDecimal());
        Assert.True(summary.GetProperty("below_minimum").GetBoolean());
        var roster = await Ok(await s.LecturerClient.GetAsync(V1 + $"/attendance/sessions/{first.TeachingSessionId}"));
        Assert.Equal("late", roster.GetProperty("students")[0].GetProperty("status").GetString());
        var classSummary = await Ok(await s.LecturerClient.GetAsync(V1 + $"/attendance/classes/{s.ClassId}/summary"));
        Assert.Equal(50, classSummary[0].GetProperty("summary").GetProperty("percentage").GetDecimal());
    }

    [Fact]
    public async Task Enrollment_approval_publishes_multiple_templates_and_revocation_changes_etag()
    {
        using var s = await Setup();
        await using var app = application.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IEmbeddingExtractor, TestExtractor>()));
        using var student = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        student.DefaultRequestHeaders.Authorization = s.StudentClient.DefaultRequestHeaders.Authorization;
        using var admin = Client(fixture.AdminToken);
        var created = await Ok(await Post(student, "/biometric-enrollments", new EnrollmentConsent(true, "research-v1")));
        var id = created.GetProperty("biometric_enrollment_id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(student, $"/biometric-enrollments/{id}/submit", new { })).StatusCode);
        string[] poses = ["frontal", "left", "right", "up", "down", "frontal", "left", "right", "up", "down", "frontal", "frontal"];
        for (var i = 0; i < poses.Length; i++)
        {
            // Both supported upload formats feed the same enrollment/gallery workflow.
            if (i % 2 == 0) await Ok(await Upload(student, id, poses[i], [(byte)i]));
            else await Ok(await Post(student, $"/biometric-enrollments/{id}/samples", new EnrollmentSample(poses[i], Convert.ToBase64String([(byte)i]))));
        }
        Assert.Equal(HttpStatusCode.Conflict, (await Post(student, $"/biometric-enrollments/{id}/samples", new EnrollmentSample("frontal", "AA=="))).StatusCode);
        var details = await Ok(await student.GetAsync(V1 + $"/biometric-enrollments/{id}"));
        Assert.DoesNotContain("embedding", details.GetRawText());
        await Ok(await Post(student, $"/biometric-enrollments/{id}/submit", new { }));
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(student, $"/biometric-enrollments/{id}/review", new EnrollmentReview("approved", "Spoof", true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(admin, $"/biometric-enrollments/{id}/review", new EnrollmentReview("approved", "Identity checked", false))).StatusCode);
        await Ok(await Post(admin, $"/biometric-enrollments/{id}/review", new EnrollmentReview("approved", "Identity checked", true)));
        var galleryResponse = await s.EdgeClient.GetAsync(V1 + "/gallery");
        var gallery = await galleryResponse.Content.ReadFromJsonAsync<GalleryDocument>(WireJson.Options);
        Assert.NotNull(gallery);
        Assert.Null(gallery.Validate(fixture.ModelHash));
        Assert.Equal(12, gallery.Templates.Count(t => t.IdentityId == s.Student.StudentIdentityId));
        var previousEtag = galleryResponse.Headers.ETag!.ToString();
        await Ok(await Post(student, $"/biometric-enrollments/{id}/revoke", new EnrollmentReason("Withdraw consent")));
        var revoked = await s.EdgeClient.GetAsync(V1 + "/gallery");
        Assert.NotEqual(previousEtag, revoked.Headers.ETag!.ToString());
        var next = await revoked.Content.ReadFromJsonAsync<GalleryDocument>(WireJson.Options);
        Assert.DoesNotContain(next!.Templates, t => t.IdentityId == s.Student.StudentIdentityId);
    }

    [Fact]
    public async Task Photo_upload_requires_own_student_and_rejects_invalid_or_duplicate_samples()
    {
        using var s = await Setup();
        using var other = await Setup();
        await using var app = application.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IEmbeddingExtractor, TestExtractor>()));
        using var student = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        student.DefaultRequestHeaders.Authorization = s.StudentClient.DefaultRequestHeaders.Authorization;
        using var admin = Client(fixture.AdminToken);
        using var anonymous = Client();
        var created = await Ok(await Post(student, "/biometric-enrollments", new EnrollmentConsent(true, "research-v1")));
        var id = created.GetProperty("biometric_enrollment_id").GetGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(anonymous, id, "frontal", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Upload(admin, id, "frontal", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(s.LecturerClient, id, "frontal", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(other.StudentClient, id, "frontal", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(student, id, "invalid-pose", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(student, id, "frontal", [1], "text/plain")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(student, id, "frontal", [])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(student, id, "frontal", new byte[5 * 1024 * 1024 + 1])).StatusCode);
        using (var missing = new MultipartFormDataContent())
        {
            missing.Add(new StringContent("frontal"), "pose");
            Assert.Equal(HttpStatusCode.BadRequest, (await student.PostAsync(V1 + $"/biometric-enrollments/{id}/samples/upload", missing)).StatusCode);
        }
        using (var multiple = new MultipartFormDataContent())
        {
            multiple.Add(new StringContent("frontal"), "pose");
            multiple.Add(new ByteArrayContent([1]), "image", "first.png");
            multiple.Add(new ByteArrayContent([2]), "another", "second.png");
            Assert.Equal(HttpStatusCode.BadRequest, (await student.PostAsync(V1 + $"/biometric-enrollments/{id}/samples/upload", multiple)).StatusCode);
        }
        // Above the framework's default 64 KiB disk-buffer threshold; still an in-memory upload.
        var sample = new byte[100 * 1024];
        sample[0] = 1;
        var result = await Ok(await Upload(student, id, "frontal", sample));
        Assert.Equal("frontal", result.GetProperty("biometric_template_pose").GetString());
        Assert.DoesNotContain("embedding", result.GetRawText());
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(student, id, "left", sample)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(student, $"/biometric-enrollments/{id}/submit", new { })).StatusCode);
        await using var db = fixture.OpenDatabase();
        var stored = await db.Set<BiometricTemplate>().SingleAsync(t => t.BiometricEnrollmentId == id);
        Assert.Equal(2048, stored.BiometricTemplateEmbedding.Length);
        Assert.False(stored.BiometricTemplateActive);
        Assert.Equal("frontal", stored.BiometricTemplatePose);
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid enrollmentId, string pose, byte[] bytes, string mediaType = "image/png")
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(pose), "pose");
        var image = new ByteArrayContent(bytes);
        image.Headers.ContentType = new(mediaType);
        content.Add(image, "image", "sample.png");
        return await client.PostAsync(V1 + $"/biometric-enrollments/{enrollmentId}/samples/upload", content);
    }

    // This double isolates enrollment transactions/publication; native inference is tested separately.
    private sealed class TestExtractor : IEmbeddingExtractor
    {
        public Task<ExtractedFace> Extract(byte[] image, CancellationToken ct)
        {
            var embedding = new byte[2048];
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(embedding.AsSpan(image[0] * 4), 1);
            return Task.FromResult(new ExtractedFace(embedding, .99, 1));
        }
    }
    private sealed record Scenario(HttpClient StudentClient, HttpClient LecturerClient, HttpClient EdgeClient, Student Student, Lecturer Lecturer,
        Guid AccountId, Guid TermId, Guid ClassId, Guid RoomId, Guid KrsId, string DeviceId, string Email) : IDisposable
    {
        public void Dispose() { StudentClient.Dispose(); LecturerClient.Dispose(); EdgeClient.Dispose(); }
    }
}
