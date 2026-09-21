using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.AcademicManagement;
using Ta.Backend.Features.Audit;
using Ta.Backend.Features.Devices;
using Ta.Backend.Features.Biometrics;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Identity;

public static class IdentityEndpoints
{
    private static readonly PasswordHasher<Account> Hasher = new();
    private static readonly string DummyHash = Hasher.HashPassword(new Account(), Credentials.Generate());

    public static void MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/auth").WithTags("Identity").AddEndpointFilter<AcademicMutationFilter>();
        auth.MapGet("/registration-options", async (BackendDbContext db, CancellationToken ct) =>
            await db.Set<StudyProgram>().Where(p => p.StudyProgramActive).Select(p => new { p.StudyProgramId, p.StudyProgramName }).ToListAsync(ct));
        auth.MapPost("/register", async (RegisterStudent request, BackendDbContext db, CancellationToken ct) =>
        {
            ValidateAccount(request.Email, request.Password, request.Name);
            DomainException.Require(WireJson.Identifier(request.StudentNumber) && request.EntryYear is >= 2000 and <= 2100, "invalid_student_profile");
            DomainException.Require(await db.Set<StudyProgram>().AnyAsync(p => p.StudyProgramId == request.StudyProgramId && p.StudyProgramActive, ct), "invalid_study_program");
            var email = request.Email.Trim().ToLowerInvariant();
            DomainException.Require(!await db.Set<Account>().AnyAsync(a => a.AccountEmail == email, ct)
                && !await db.Set<Student>().AnyAsync(s => s.StudentNumber == request.StudentNumber, ct), "registration_conflict", 409);
            var user = NewAccount(email, request.Password, request.Name, Roles.Student, "pending");
            db.Add(user);
            db.Add(new Student { AccountId = user.AccountId, StudyProgramId = request.StudyProgramId,
                StudentNumber = request.StudentNumber, StudentEntryYear = request.EntryYear });
            return Results.Created($"{ApiRoutes.V1}/auth/me", new { user.AccountId, user.AccountStatus });
        }).WithSummary("Register a student account for administrator approval");

        auth.MapPost("/login", async (LoginRequest request, BackendDbContext db, CancellationToken ct) =>
        {
            if (request.Email is null || request.Password is null || request.Password.Length > 128) return Results.Unauthorized();
            var email = request.Email.Trim().ToLowerInvariant();
            var user = await db.Set<Account>().SingleOrDefaultAsync(a => a.AccountEmail == email, ct);
            var verified = Hasher.VerifyHashedPassword(user ?? new Account(), user?.AccountPasswordHash ?? DummyHash, request.Password);
            if (user is null) return Results.Unauthorized();
            if (user.AccountLockedUntil > DateTimeOffset.UtcNow) return Results.Unauthorized();
            if (verified == PasswordVerificationResult.Failed)
            {
                user.AccountFailedLogins++;
                if (user.AccountFailedLogins >= 5) { user.AccountLockedUntil = DateTimeOffset.UtcNow.AddMinutes(15); user.AccountFailedLogins = 0; }
                return Results.Unauthorized();
            }
            if (user.AccountStatus != "approved") return Results.Json(new ApiError("account_not_approved"), statusCode: 403);
            user.AccountFailedLogins = 0;
            user.AccountLockedUntil = null;
            if (verified == PasswordVerificationResult.SuccessRehashNeeded) user.AccountPasswordHash = Hasher.HashPassword(user, request.Password);
            var token = Credentials.Generate();
            var expires = DateTimeOffset.UtcNow.AddHours(8);
            db.Add(new AccountSession { AccountId = user.AccountId, AccountSessionTokenHash = Credentials.Hash(token), AccountSessionExpiresAt = expires });
            db.Add(new AuditRecord { AuditActor = user.AccountId.ToString(), AuditAction = "login", AuditResource = "account_session" });
            return Results.Ok(new { access_token = token, token_type = "Bearer", expires_at = expires, role = user.AccountRole, account_id = user.AccountId });
        }).WithSummary("Sign in with an approved human account");

        auth.MapGet("/me", async (AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
            await db.Set<Account>().Where(a => a.AccountId == access.AccountId)
                .Select(a => new { a.AccountId, a.AccountEmail, a.AccountName, a.AccountRole, a.AccountStatus }).SingleAsync(ct))
            .RequireAuthorization(Roles.HumanPolicy);
        auth.MapPost("/logout", async (HttpContext context, BackendDbContext db, CancellationToken ct) =>
        {
            var id = Guid.Parse(context.User.FindFirstValue("session_id")!);
            (await db.Set<AccountSession>().SingleAsync(s => s.AccountSessionId == id, ct)).AccountSessionRevoked = true;
            return Results.NoContent();
        }).RequireAuthorization(Roles.HumanPolicy);
        auth.MapPut("/password", async (ChangePassword request, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var user = await db.Set<Account>().SingleAsync(a => a.AccountId == access.AccountId, ct);
            DomainException.Require(request.CurrentPassword is not null && request.CurrentPassword.Length <= 128
                && Hasher.VerifyHashedPassword(user, user.AccountPasswordHash, request.CurrentPassword) != PasswordVerificationResult.Failed, "invalid_current_password");
            ValidateAccount(user.AccountEmail, request.NewPassword, user.AccountName);
            user.AccountPasswordHash = Hasher.HashPassword(user, request.NewPassword);
            await db.Set<AccountSession>().Where(s => s.AccountId == user.AccountId).ExecuteUpdateAsync(s => s.SetProperty(x => x.AccountSessionRevoked, true), ct);
            return Results.NoContent();
        }).RequireAuthorization(Roles.HumanPolicy);

        var admin = endpoints.MapGroup("/admin/accounts").WithTags("Identity").RequireAuthorization(Roles.AdministratorPolicy)
            .AddEndpointFilter<AcademicMutationFilter>();
        admin.MapGet("/", async (int? page, BackendDbContext db, CancellationToken ct) =>
            await db.Set<Account>().OrderBy(a => a.AccountCreatedAt).Skip(Math.Max(0, (page ?? 1) - 1) * 100).Take(100)
                .Select(a => new { a.AccountId, a.AccountEmail, a.AccountName, a.AccountRole, a.AccountStatus }).ToListAsync(ct));
        admin.MapPost("/", async (CreateStaff request, BackendDbContext db, CancellationToken ct) =>
        {
            ValidateAccount(request.Email, request.Password, request.Name);
            DomainException.Require(request.Role is Roles.Administrator or Roles.Lecturer, "invalid_staff_role");
            var email = request.Email.Trim().ToLowerInvariant();
            DomainException.Require(!await db.Set<Account>().AnyAsync(a => a.AccountEmail == email, ct), "account_exists", 409);
            if (request.Role == Roles.Lecturer) DomainException.Require(WireJson.Identifier(request.LecturerNumber)
                && !await db.Set<Lecturer>().AnyAsync(l => l.LecturerNumber == request.LecturerNumber, ct), "invalid_or_duplicate_lecturer_number");
            var user = NewAccount(email, request.Password, request.Name, request.Role, "approved");
            db.Add(user);
            if (request.Role == Roles.Lecturer) db.Add(new Lecturer { AccountId = user.AccountId, LecturerNumber = request.LecturerNumber! });
            return Results.Ok(new { user.AccountId });
        });
        admin.MapPut("/{id:guid}/status", async (Guid id, AccountStatus request, AcademicAccess access, BackendDbContext db, GalleryPublisher gallery, CancellationToken ct) =>
        {
            DomainException.Require(request.Status is "approved" or "rejected" or "disabled", "invalid_account_status");
            DomainException.Require(WireJson.Identifier(request.Reason), "reason_required");
            DomainException.Require(id != access.AccountId, "cannot_disable_own_account");
            var user = await db.Set<Account>().FindAsync([id], ct) ?? throw new DomainException("account_not_found", 404);
            var previous = user.AccountStatus;
            user.AccountStatus = request.Status;
            if (request.Status != "approved")
            {
                await db.Set<AccountSession>().Where(s => s.AccountId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AccountSessionRevoked, true), ct);
                var student = await db.Set<Student>().SingleOrDefaultAsync(s => s.AccountId == id, ct);
                if (student is not null)
                {
                    var now = DateTimeOffset.UtcNow;
                    await db.Set<ClassMembership>().Where(m => m.StudentId == student.StudentId && m.ClassMembershipValidUntil == null)
                        .ExecuteUpdateAsync(m => m.SetProperty(x => x.ClassMembershipValidUntil, now), ct);
                    await db.Set<CourseRegistration>().Where(r => r.StudentId == student.StudentId && r.CourseRegistrationStatus == "approved")
                        .ExecuteUpdateAsync(r => r.SetProperty(x => x.CourseRegistrationStatus, "withdrawn"), ct);
                    await db.Set<BiometricTemplate>().Where(t => db.Set<BiometricEnrollment>().Any(e => e.StudentId == student.StudentId && e.BiometricEnrollmentId == t.BiometricEnrollmentId))
                        .ExecuteUpdateAsync(t => t.SetProperty(x => x.BiometricTemplateActive, false), ct);
                    await gallery.Publish(ct);
                }
            }
            AuditLog.Add(db, access.User, "account_status", id.ToString(), new { previous, request.Status, request.Reason });
            return Results.NoContent();
        });
        admin.MapPut("/{id:guid}/profile", async (Guid id, AccountProfile request, BackendDbContext db, CancellationToken ct) =>
        {
            DomainException.Require(WireJson.Identifier(request.Name), "invalid_name");
            var user = await db.Set<Account>().FindAsync([id], ct) ?? throw new DomainException("account_not_found", 404);
            user.AccountName = request.Name;
            return Results.NoContent();
        });
    }

    private static Account NewAccount(string email, string password, string name, string role, string status)
    {
        var account = new Account { AccountEmail = email, AccountName = name.Trim(), AccountRole = role, AccountStatus = status };
        account.AccountPasswordHash = Hasher.HashPassword(account, password);
        return account;
    }
    private static void ValidateAccount(string? email, string? password, string? name)
    {
        DomainException.Require(email is { Length: <= 254 } && MailAddress.TryCreate(email, out var parsed) && parsed.Address == email.Trim(), "invalid_email");
        DomainException.Require(password is { Length: >= 12 and <= 128 }, "password_must_be_12_to_128_characters");
        DomainException.Require(WireJson.Identifier(name), "invalid_name");
    }
}

public sealed record RegisterStudent(string Email, string Password, string Name, string StudentNumber, Guid StudyProgramId, int EntryYear);
public sealed record LoginRequest(string Email, string Password);
public sealed record ChangePassword(string CurrentPassword, string NewPassword);
public sealed record CreateStaff(string Email, string Password, string Name, string Role, string? LecturerNumber);
public sealed record AccountStatus(string Status, string Reason);
public sealed record AccountProfile(string Name);
