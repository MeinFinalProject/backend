using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.Identity;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.AcademicManagement;

public sealed class AcademicAccess(BackendDbContext db, IHttpContextAccessor accessor)
{
    public ClaimsPrincipal User => accessor.HttpContext!.User;
    public bool IsAdmin => User.IsInRole(Roles.Administrator);
    public Guid AccountId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    public async Task<Student> Student(CancellationToken ct) => await db.Set<Student>().SingleOrDefaultAsync(s => s.AccountId == AccountId, ct)
        ?? throw new DomainException("student_profile_required", 403);
    public async Task<Lecturer> Lecturer(CancellationToken ct) => await db.Set<Lecturer>().SingleOrDefaultAsync(s => s.AccountId == AccountId, ct)
        ?? throw new DomainException("lecturer_profile_required", 403);
    public async Task<AcademicClass> Class(Guid id, CancellationToken ct)
    {
        var item = await db.Set<AcademicClass>().FindAsync([id], ct) ?? throw new DomainException("class_not_found", 404);
        if (!IsAdmin) DomainException.Require(item.LecturerId == (await Lecturer(ct)).LecturerId, "teaching_assignment_required", 403);
        return item;
    }
    public async Task<TeachingSession> Session(Guid id, CancellationToken ct)
    {
        var item = await db.Set<TeachingSession>().FindAsync([id], ct) ?? throw new DomainException("session_not_found", 404);
        if (!IsAdmin) DomainException.Require(item.LecturerId == (await Lecturer(ct)).LecturerId, "teaching_assignment_required", 403);
        return item;
    }
    public async Task ReviewStudent(Guid studentId, CancellationToken ct)
    {
        var student = await db.Set<Student>().FindAsync([studentId], ct) ?? throw new DomainException("student_not_found", 404);
        if (!IsAdmin) DomainException.Require(student.AdvisorLecturerId == (await Lecturer(ct)).LecturerId, "advisor_assignment_required", 403);
    }
}
