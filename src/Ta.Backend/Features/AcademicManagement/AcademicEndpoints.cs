using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.Identity;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.AcademicManagement;

public static class AcademicEndpoints
{
    public static void MapAcademicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/academic").WithTags("Academic Management").AddEndpointFilter<AcademicMutationFilter>();
        Catalog<StudyProgram>(group, "/study-programs", async (p, db, ct) =>
        {
            Text(p.StudyProgramCode, p.StudyProgramName);
            DomainException.Require(!await db.Set<StudyProgram>().AnyAsync(x => x.StudyProgramCode == p.StudyProgramCode && x.StudyProgramId != p.StudyProgramId, ct), "program_code_exists", 409);
        });
        Catalog<AcademicTerm>(group, "/terms", async (t, db, ct) =>
        {
            Text(t.AcademicTermName);
            DomainException.Require(t.AcademicTermEnd >= t.AcademicTermStart && t.AcademicTermMinimumAttendance is >= 0 and <= 100, "invalid_term");
            var previous = await db.Set<AcademicTerm>().AsNoTracking().SingleOrDefaultAsync(x => x.AcademicTermId == t.AcademicTermId, ct);
            var sessions = await (from s in db.Set<TeachingSession>() join c in db.Set<AcademicClass>() on s.AcademicClassId equals c.AcademicClassId
                where c.AcademicTermId == t.AcademicTermId select s.TeachingSessionStart).ToListAsync(ct);
            if (previous is not null && sessions.Any(at => at <= DateTimeOffset.UtcNow))
                DomainException.Require(t.AcademicTermStart == previous.AcademicTermStart && t.AcademicTermEnd == previous.AcademicTermEnd
                    && t.AcademicTermMinimumAttendance == previous.AcademicTermMinimumAttendance && t.AcademicTermName == previous.AcademicTermName,
                    "historical_term_is_immutable", 409);
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jakarta");
            DomainException.Require(sessions.All(at => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime) >= t.AcademicTermStart
                && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime) <= t.AcademicTermEnd), "term_excludes_existing_sessions", 409);
        });
        Catalog<Course>(group, "/courses", async (c, db, ct) =>
        {
            Text(c.CourseCode, c.CourseName);
            DomainException.Require(c.CourseCredits is >= 1 and <= 24 && await db.Set<StudyProgram>().AnyAsync(p => p.StudyProgramId == c.StudyProgramId, ct), "invalid_course");
            DomainException.Require(!await db.Set<Course>().AnyAsync(x => x.CourseCode == c.CourseCode && x.CourseId != c.CourseId, ct), "course_code_exists", 409);
        });
        Catalog<Classroom>(group, "/classrooms", async (r, db, ct) =>
        {
            Text(r.ClassroomCode, r.ClassroomName);
            DomainException.Require(!await db.Set<Classroom>().AnyAsync(x => x.ClassroomCode == r.ClassroomCode && x.ClassroomId != r.ClassroomId, ct), "room_code_exists", 409);
        });
        Catalog<AcademicClass>(group, "/classes", async (c, db, ct) =>
        {
            Text(c.AcademicClassName);
            DomainException.Require(c.AcademicClassCapacity is >= 1 and <= 1000, "invalid_capacity");
            DomainException.Require(await db.Set<AcademicTerm>().AnyAsync(t => t.AcademicTermId == c.AcademicTermId && t.AcademicTermActive, ct)
                && await db.Set<Course>().AnyAsync(x => x.CourseId == c.CourseId && x.CourseActive, ct)
                && await (from l in db.Set<Lecturer>() join a in db.Set<Account>() on l.AccountId equals a.AccountId
                    where l.LecturerId == c.LecturerId && a.AccountStatus == "approved" select l).AnyAsync(ct), "invalid_class_references");
            var previous = await db.Set<AcademicClass>().AsNoTracking().SingleOrDefaultAsync(x => x.AcademicClassId == c.AcademicClassId, ct);
            if (previous is not null) DomainException.Require(previous.CourseId == c.CourseId && previous.AcademicTermId == c.AcademicTermId,
                "class_course_and_term_are_immutable", 409);
            DomainException.Require(await db.Set<ClassMembership>().CountAsync(m => m.AcademicClassId == c.AcademicClassId && m.ClassMembershipValidUntil == null, ct)
                <= c.AcademicClassCapacity, "capacity_below_enrollment", 409);
        });

        group.MapGet("/students", async (int? page, BackendDbContext db, CancellationToken ct) =>
            await (from s in db.Set<Student>() join a in db.Set<Account>() on s.AccountId equals a.AccountId
                orderby s.StudentNumber select new { student = s, a.AccountName, a.AccountStatus })
                .Skip(Math.Max(0, (page ?? 1) - 1) * 100).Take(100).ToListAsync(ct)).RequireAuthorization(Roles.AdministratorPolicy);
        group.MapGet("/advisees", async (AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var lecturer = await access.Lecturer(ct);
            return await (from s in db.Set<Student>() join a in db.Set<Account>() on s.AccountId equals a.AccountId
                where s.AdvisorLecturerId == lecturer.LecturerId
                orderby s.StudentNumber select new { student = s, a.AccountName, a.AccountStatus }).ToListAsync(ct);
        }).RequireAuthorization(Roles.StaffPolicy);
        group.MapGet("/lecturers", async (BackendDbContext db, CancellationToken ct) =>
            await (from l in db.Set<Lecturer>() join a in db.Set<Account>() on l.AccountId equals a.AccountId
                orderby l.LecturerNumber select new { l.LecturerId, l.LecturerNumber, l.AccountId, a.AccountName }).ToListAsync(ct))
            .RequireAuthorization("Academic");
        group.MapPut("/students/{id:guid}", async (Guid id, StudentAdministration request, BackendDbContext db, CancellationToken ct) =>
        {
            var student = await db.Set<Student>().FindAsync([id], ct) ?? throw new DomainException("student_not_found", 404);
            DomainException.Require(request.AdvisorLecturerId is null || await db.Set<Lecturer>().AnyAsync(l => l.LecturerId == request.AdvisorLecturerId, ct), "invalid_advisor");
            student.AdvisorLecturerId = request.AdvisorLecturerId;
            if (request.StudentNumber is not null)
            {
                Text(request.StudentNumber);
                DomainException.Require(!await db.Set<Student>().AnyAsync(s => s.StudentId != id && s.StudentNumber == request.StudentNumber, ct), "student_number_exists", 409);
                student.StudentNumber = request.StudentNumber;
            }
            if (request.StudyProgramId.HasValue)
            {
                DomainException.Require(await db.Set<StudyProgram>().AnyAsync(p => p.StudyProgramId == request.StudyProgramId && p.StudyProgramActive, ct), "invalid_study_program");
                student.StudyProgramId = request.StudyProgramId.Value;
            }
            if (request.EntryYear.HasValue)
            {
                DomainException.Require(request.EntryYear is >= 2000 and <= 2100, "invalid_entry_year");
                student.StudentEntryYear = request.EntryYear.Value;
            }
            return Results.NoContent();
        }).RequireAuthorization(Roles.AdministratorPolicy);
        group.MapPut("/lecturers/{id:guid}", async (Guid id, LecturerProfile request, BackendDbContext db, CancellationToken ct) =>
        {
            Text(request.LecturerNumber);
            var lecturer = await db.Set<Lecturer>().FindAsync([id], ct) ?? throw new DomainException("lecturer_not_found", 404);
            DomainException.Require(!await db.Set<Lecturer>().AnyAsync(l => l.LecturerId != id && l.LecturerNumber == request.LecturerNumber, ct), "lecturer_number_exists", 409);
            lecturer.LecturerNumber = request.LecturerNumber;
            return Results.NoContent();
        }).RequireAuthorization(Roles.AdministratorPolicy);
        group.MapGet("/my-profile", async (AcademicAccess access, CancellationToken ct) => await access.Student(ct))
            .RequireAuthorization(Roles.StudentPolicy);
        group.MapPut("/my-profile", async (StudentProfile request, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            Text(request.Name);
            DomainException.Require(request.Phone is { Length: <= 32 }, "invalid_phone");
            var student = await access.Student(ct);
            student.StudentPhone = request.Phone;
            (await db.Set<Account>().SingleAsync(a => a.AccountId == access.AccountId, ct)).AccountName = request.Name;
            return Results.NoContent();
        }).RequireAuthorization(Roles.StudentPolicy);
        group.MapGet("/my-classes", async (AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            if (access.IsAdmin) return Results.Ok(await db.Set<AcademicClass>().ToListAsync(ct));
            if (access.User.IsInRole(Roles.Lecturer))
            {
                var lecturer = await access.Lecturer(ct);
                return Results.Ok(await db.Set<AcademicClass>().Where(c => c.LecturerId == lecturer.LecturerId).ToListAsync(ct));
            }
            var student = await access.Student(ct);
            return Results.Ok(await (from m in db.Set<ClassMembership>() join c in db.Set<AcademicClass>() on m.AcademicClassId equals c.AcademicClassId
                where m.StudentId == student.StudentId && m.ClassMembershipValidUntil == null select c).ToListAsync(ct));
        }).RequireAuthorization("Academic");
    }

    // Only small administrative reference catalogs share this CRUD wiring. Workflow
    // aggregates (KRS, memberships, sessions, biometrics) have explicit transitions.
    private static void Catalog<T>(RouteGroupBuilder group, string path,
        Func<T, BackendDbContext, CancellationToken, Task> validate) where T : class
    {
        group.MapGet(path + "/{id:guid}", async (Guid id, BackendDbContext db, CancellationToken ct) =>
            await db.Set<T>().FindAsync([id], ct) is {} record ? Results.Ok(record) : Results.NotFound())
            .RequireAuthorization("Academic").Produces<T>();
        group.MapGet(path, async (int? page, BackendDbContext db, CancellationToken ct) =>
            await db.Set<T>().AsNoTracking().OrderBy(x => EF.Property<Guid>(x, typeof(T).Name + "Id"))
                .Skip(Math.Max(0, (page ?? 1) - 1) * 100).Take(100).ToListAsync(ct)).RequireAuthorization("Academic");
        group.MapPost(path, async (T request, BackendDbContext db, CancellationToken ct) =>
        {
            typeof(T).GetProperty(typeof(T).Name + "Id")!.SetValue(request, Guid.NewGuid());
            await validate(request, db, ct);
            db.Add(request);
            return Results.Ok(request);
        }).RequireAuthorization(Roles.AdministratorPolicy).Produces<T>();
        group.MapPut(path + "/{id:guid}", async (Guid id, T request, BackendDbContext db, CancellationToken ct) =>
        {
            var current = await db.Set<T>().FindAsync([id], ct) ?? throw new DomainException("record_not_found", 404);
            typeof(T).GetProperty(typeof(T).Name + "Id")!.SetValue(request, id);
            await validate(request, db, ct);
            db.Entry(current).CurrentValues.SetValues(request);
            return Results.Ok(current);
        }).RequireAuthorization(Roles.AdministratorPolicy).Produces<T>();
    }
    private static void Text(params string[] values) => DomainException.Require(values.All(WireJson.Identifier), "invalid_text");
}
public sealed record StudentAdministration(Guid? AdvisorLecturerId, string? StudentNumber = null, Guid? StudyProgramId = null, int? EntryYear = null);
public sealed record LecturerProfile(string LecturerNumber);
public sealed record StudentProfile(string Name, string Phone);
