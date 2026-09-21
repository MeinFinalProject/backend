using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.Audit;
using Ta.Backend.Features.Identity;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.AcademicManagement;

public static class RegistrationEndpoints
{
    public static void MapRegistrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/course-registrations").WithTags("Course Registration")
            .RequireAuthorization("Academic").AddEndpointFilter<AcademicMutationFilter>();
        group.MapGet("/", async (AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var query = db.Set<CourseRegistration>().AsQueryable();
            if (!access.IsAdmin)
            {
                if (access.User.IsInRole(Roles.Student))
                {
                    var student = await access.Student(ct);
                    query = query.Where(r => r.StudentId == student.StudentId);
                }
                else
                {
                    var lecturer = await access.Lecturer(ct);
                    query = query.Where(r => db.Set<Student>().Any(s => s.StudentId == r.StudentId && s.AdvisorLecturerId == lecturer.LecturerId));
                }
            }
            return await query.OrderByDescending(r => r.CourseRegistrationSubmittedAt).Take(200).ToListAsync(ct);
        });
        group.MapGet("/{id:guid}", async (Guid id, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var registration = await Load(id, db, ct);
            await Authorize(registration, access, ct);
            return Results.Ok(new { registration, classes = await db.Set<CourseRegistrationItem>().Where(i => i.CourseRegistrationId == id).ToListAsync(ct) });
        });
        group.MapPost("/", async (SaveRegistration request, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var student = await access.Student(ct);
            var registration = await db.Set<CourseRegistration>().SingleOrDefaultAsync(r => r.StudentId == student.StudentId && r.AcademicTermId == request.AcademicTermId, ct);
            if (registration is null)
            {
                registration = new CourseRegistration { StudentId = student.StudentId, AcademicTermId = request.AcademicTermId };
                db.Add(registration);
            }
            DomainException.Require(registration.CourseRegistrationStatus is "draft" or "corrections" or "withdrawn", "registration_not_editable", 409);
            var term = await db.Set<AcademicTerm>().FindAsync([request.AcademicTermId], ct);
            DomainException.Require(term is { AcademicTermActive: true } && term.AcademicTermEnd >= AcademicToday(), "term_closed");
            DomainException.Require(request.ClassIds is { Length: > 0 and <= 20 } && request.ClassIds.Distinct().Count() == request.ClassIds.Length, "invalid_class_selection");
            var classes = await db.Set<AcademicClass>().Where(c => request.ClassIds.Contains(c.AcademicClassId) && c.AcademicTermId == request.AcademicTermId && c.AcademicClassActive).ToListAsync(ct);
            DomainException.Require(classes.Count == request.ClassIds.Length && classes.Select(c => c.CourseId).Distinct().Count() == classes.Count, "invalid_or_duplicate_course");
            var programCourses = await db.Set<Course>().Where(c => c.StudyProgramId == student.StudyProgramId && c.CourseActive).Select(c => c.CourseId).ToListAsync(ct);
            DomainException.Require(classes.All(c => programCourses.Contains(c.CourseId)), "course_outside_study_program");
            await db.Set<CourseRegistrationItem>().Where(i => i.CourseRegistrationId == registration.CourseRegistrationId).ExecuteDeleteAsync(ct);
            foreach (var classId in request.ClassIds) db.Add(new CourseRegistrationItem { CourseRegistrationId = registration.CourseRegistrationId, AcademicClassId = classId });
            registration.CourseRegistrationStatus = "draft";
            return Results.Ok(new { registration.CourseRegistrationId });
        }).RequireAuthorization(Roles.StudentPolicy);
        group.MapPost("/{id:guid}/submit", async (Guid id, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var registration = await Load(id, db, ct);
            DomainException.Require(registration.StudentId == (await access.Student(ct)).StudentId, "not_your_registration", 403);
            DomainException.Require(registration.CourseRegistrationStatus is "draft" or "corrections", "registration_not_editable", 409);
            DomainException.Require(await db.Set<CourseRegistrationItem>().AnyAsync(i => i.CourseRegistrationId == id, ct), "registration_empty");
            registration.CourseRegistrationStatus = "submitted";
            registration.CourseRegistrationSubmittedAt = DateTimeOffset.UtcNow;
            return Results.NoContent();
        }).RequireAuthorization(Roles.StudentPolicy);
        group.MapPost("/{id:guid}/review", async (Guid id, RegistrationReview request, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var registration = await Load(id, db, ct);
            await access.ReviewStudent(registration.StudentId, ct);
            DomainException.Require(registration.CourseRegistrationStatus == "submitted", "registration_not_submitted", 409);
            DomainException.Require(request.Decision is "approved" or "corrections" && WireJson.Identifier(request.Note), "invalid_review");
            if (request.Decision == "approved")
            {
                var student = await db.Set<Student>().SingleAsync(s => s.StudentId == registration.StudentId, ct);
                DomainException.Require(await db.Set<Account>().AnyAsync(a => a.AccountId == student.AccountId && a.AccountStatus == "approved", ct), "student_not_approved");
                var term = await db.Set<AcademicTerm>().SingleAsync(t => t.AcademicTermId == registration.AcademicTermId, ct);
                DomainException.Require(term.AcademicTermActive && term.AcademicTermEnd >= AcademicToday(), "term_closed");
                var classes = await (from i in db.Set<CourseRegistrationItem>() join c in db.Set<AcademicClass>() on i.AcademicClassId equals c.AcademicClassId
                    where i.CourseRegistrationId == id select c).ToListAsync(ct);
                var eligibleCourses = await db.Set<Course>().Where(c => c.StudyProgramId == student.StudyProgramId && c.CourseActive)
                    .Select(c => c.CourseId).ToListAsync(ct);
                DomainException.Require(classes.Count > 0 && classes.All(c => eligibleCourses.Contains(c.CourseId)), "course_selection_no_longer_valid", 409);
                foreach (var item in classes)
                {
                    DomainException.Require(item.AcademicClassActive && await db.Set<ClassMembership>().CountAsync(m => m.AcademicClassId == item.AcademicClassId && m.ClassMembershipValidUntil == null, ct) < item.AcademicClassCapacity,
                        "class_unavailable_or_full", 409);
                    db.Add(new ClassMembership { StudentId = registration.StudentId, AcademicClassId = item.AcademicClassId,
                        CourseRegistrationId = id, ClassMembershipValidFrom = DateTimeOffset.UtcNow });
                }
            }
            registration.CourseRegistrationStatus = request.Decision;
            registration.CourseRegistrationReviewNote = request.Note;
            registration.CourseRegistrationReviewedAt = DateTimeOffset.UtcNow;
            registration.CourseRegistrationReviewerId = access.AccountId == Guid.Empty ? null : access.AccountId;
            AuditLog.Add(db, access.User, "krs_review", id.ToString(), request);
            return Results.NoContent();
        }).RequireAuthorization(Roles.StaffPolicy);
        group.MapPost("/{id:guid}/withdraw", async (Guid id, ReviewNote request, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var registration = await Load(id, db, ct);
            await access.ReviewStudent(registration.StudentId, ct);
            DomainException.Require(registration.CourseRegistrationStatus == "approved" && WireJson.Identifier(request.Note), "invalid_withdrawal", 409);
            registration.CourseRegistrationStatus = "withdrawn";
            var now = DateTimeOffset.UtcNow;
            await db.Set<ClassMembership>().Where(m => m.CourseRegistrationId == id && m.ClassMembershipValidUntil == null)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ClassMembershipValidUntil, now), ct);
            AuditLog.Add(db, access.User, "krs_withdrawal", id.ToString(), request);
            return Results.NoContent();
        }).RequireAuthorization(Roles.StaffPolicy);
    }
    private static DateOnly AcademicToday() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Jakarta")).DateTime);
    private static async Task<CourseRegistration> Load(Guid id, BackendDbContext db, CancellationToken ct) =>
        await db.Set<CourseRegistration>().FindAsync([id], ct) ?? throw new DomainException("registration_not_found", 404);
    private static async Task Authorize(CourseRegistration registration, AcademicAccess access, CancellationToken ct)
    {
        if (access.User.IsInRole(Roles.Student)) DomainException.Require(registration.StudentId == (await access.Student(ct)).StudentId, "not_your_registration", 403);
        else await access.ReviewStudent(registration.StudentId, ct);
    }
}
public sealed record SaveRegistration(Guid AcademicTermId, Guid[] ClassIds);
public sealed record RegistrationReview(string Decision, string Note);
public sealed record ReviewNote(string Note);
