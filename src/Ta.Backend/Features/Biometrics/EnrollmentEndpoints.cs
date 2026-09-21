using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.AcademicManagement;
using Ta.Backend.Features.Audit;
using Ta.Backend.Features.Identity;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Biometrics;

public static class EnrollmentEndpoints
{
    private const int MaxImageBytes = 5 * 1024 * 1024;
    private static readonly string[] Poses = ["frontal", "left", "right", "up", "down"];
    public static void MapEnrollmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/biometric-enrollments").WithTags("Biometric enrollment").RequireAuthorization("Academic")
            .AddEndpointFilter<AcademicMutationFilter>();
        group.MapGet("/my-status", async (AcademicAccess access, BackendDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var student = await access.Student(ct);
            var latest = await db.Set<BiometricEnrollment>().AsNoTracking().Where(e => e.StudentId == student.StudentId)
                .OrderByDescending(e => e.BiometricEnrollmentCreatedAt).ThenByDescending(e => e.BiometricEnrollmentId).FirstOrDefaultAsync(ct);
            var sampleCount = latest is null ? 0 : await db.Set<BiometricTemplate>().CountAsync(t => t.BiometricEnrollmentId == latest.BiometricEnrollmentId, ct);
            var activeCount = await (from t in db.Set<BiometricTemplate>() join e in db.Set<BiometricEnrollment>() on t.BiometricEnrollmentId equals e.BiometricEnrollmentId
                where e.StudentId == student.StudentId && e.BiometricEnrollmentStatus == "approved" && t.BiometricTemplateActive
                    && t.BiometricTemplateModelSha256 == config["Biometrics:ModelSha256"] select t.BiometricTemplateId).CountAsync(ct);
            // Enrollment readiness is not proof that any Edge device has installed a gallery.
            return Results.Ok(new { latest_enrollment = latest, latest_sample_count = sampleCount,
                active_sample_count = activeCount, required_samples = 12 });
        }).RequireAuthorization(Roles.StudentPolicy);
        group.MapGet("/", async (AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var query = db.Set<BiometricEnrollment>().AsNoTracking();
            if (!access.IsAdmin) { var student = await access.Student(ct); query = query.Where(e => e.StudentId == student.StudentId); }
            return Results.Ok(await query.OrderByDescending(e => e.BiometricEnrollmentCreatedAt).Take(100).ToListAsync(ct));
        });
        group.MapGet("/{id:guid}", async (Guid id, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var enrollment = await OwnEnrollment(id, access, db, ct);
            var samples = await db.Set<BiometricTemplate>().Where(t => t.BiometricEnrollmentId == id)
                .Select(t => new { t.BiometricTemplateId, t.BiometricTemplatePose, t.BiometricTemplateActive, t.BiometricTemplateDetectionScore,
                    t.BiometricTemplateAlignmentError, t.BiometricTemplateCreatedAt }).ToListAsync(ct);
            return Results.Ok(new { enrollment, samples, required_samples = 12, minimum_per_orientation = 2, orientations = Poses });
        });
        group.MapPost("/", async (EnrollmentConsent request, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            DomainException.Require(request.Accepted && request.Version == "research-v1", "biometric_consent_required");
            var student = await access.Student(ct);
            DomainException.Require(!await db.Set<BiometricEnrollment>().AnyAsync(e => e.StudentId == student.StudentId && (e.BiometricEnrollmentStatus == "draft" || e.BiometricEnrollmentStatus == "submitted"), ct), "enrollment_already_open", 409);
            var enrollment = new BiometricEnrollment { StudentId = student.StudentId };
            db.Add(enrollment);
            return Results.Created($"{ApiRoutes.V1}/biometric-enrollments/{enrollment.BiometricEnrollmentId}", enrollment);
        }).RequireAuthorization(Roles.StudentPolicy);
        group.MapDelete("/{id:guid}/samples/{sampleId:guid}", async (Guid id, Guid sampleId, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var enrollment = await OwnEnrollment(id, access, db, ct);
            DomainException.Require(enrollment.BiometricEnrollmentStatus == "draft", "enrollment_not_draft", 409);
            var sample = await db.Set<BiometricTemplate>().SingleOrDefaultAsync(t => t.BiometricTemplateId == sampleId && t.BiometricEnrollmentId == id, ct)
                ?? throw new DomainException("sample_not_found", 404);
            db.Remove(sample);
            return Results.NoContent();
        });
        group.MapPost("/{id:guid}/submit", async (Guid id, AcademicAccess access, BackendDbContext db, CancellationToken ct) =>
        {
            var enrollment = await OwnEnrollment(id, access, db, ct);
            DomainException.Require(enrollment.BiometricEnrollmentStatus == "draft", "enrollment_not_draft", 409);
            var poses = await db.Set<BiometricTemplate>().Where(t => t.BiometricEnrollmentId == id).Select(t => t.BiometricTemplatePose).ToListAsync(ct);
            DomainException.Require(poses.Count == 12 && Poses.All(p => poses.Count(x => x == p) >= 2), "twelve_varied_samples_required");
            enrollment.BiometricEnrollmentStatus = "submitted";
            return Results.NoContent();
        });
        group.MapPost("/{id:guid}/review", async (Guid id, EnrollmentReview request, AcademicAccess access, BackendDbContext db, GalleryPublisher gallery, IConfiguration config, CancellationToken ct) =>
        {
            var enrollment = await OwnEnrollment(id, access, db, ct);
            DomainException.Require(enrollment.BiometricEnrollmentStatus == "submitted" && request.Decision is "approved" or "rejected" && WireJson.Identifier(request.Note), "invalid_enrollment_review", 409);
            var samples = await db.Set<BiometricTemplate>().Where(t => t.BiometricEnrollmentId == id).ToListAsync(ct);
            if (request.Decision == "approved")
            {
                DomainException.Require(request.IdentityVerified, "administrator_identity_verification_required");
                DomainException.Require(samples.Count == 12 && samples.All(t => t.BiometricTemplateModelSha256 == config["Biometrics:ModelSha256"]), "enrollment_model_changed_reenroll", 409);
                var student = await db.Set<Student>().SingleAsync(s => s.StudentId == enrollment.StudentId, ct);
                DomainException.Require(await db.Set<Account>().AnyAsync(a => a.AccountId == student.AccountId && a.AccountStatus == "approved", ct), "account_not_approved", 409);
                var previous = await db.Set<BiometricEnrollment>().Where(e => e.StudentId == enrollment.StudentId && e.BiometricEnrollmentStatus == "approved").ToListAsync(ct);
                foreach (var old in previous) old.BiometricEnrollmentStatus = "revoked";
                var previousIds = previous.Select(e => e.BiometricEnrollmentId).ToArray();
                await db.Set<BiometricTemplate>().Where(t => previousIds.Contains(t.BiometricEnrollmentId)).ExecuteUpdateAsync(t => t.SetProperty(x => x.BiometricTemplateActive, false), ct);
                foreach (var sample in samples) sample.BiometricTemplateActive = true;
            }
            enrollment.BiometricEnrollmentStatus = request.Decision;
            enrollment.BiometricEnrollmentReviewNote = request.Note;
            AuditLog.Add(db, access.User, "biometrics.review", id.ToString(), request);
            var publication = request.Decision == "approved" ? await gallery.Publish(ct) : null;
            return Results.Ok(new { enrollment, publication });
        }).RequireAuthorization(Roles.AdministratorPolicy);
        group.MapPost("/{id:guid}/revoke", async (Guid id, EnrollmentReason request, AcademicAccess access, BackendDbContext db, GalleryPublisher gallery, CancellationToken ct) =>
        {
            var enrollment = await OwnEnrollment(id, access, db, ct);
            DomainException.Require(WireJson.Identifier(request.Reason), "reason_required");
            enrollment.BiometricEnrollmentStatus = "revoked";
            await db.Set<BiometricTemplate>().Where(t => t.BiometricEnrollmentId == id).ExecuteUpdateAsync(t => t.SetProperty(x => x.BiometricTemplateActive, false), ct);
            AuditLog.Add(db, access.User, "biometrics.revoke", id.ToString(), request);
            return Results.Ok(await gallery.Publish(ct));
        });
        group.MapPost("/{id:guid}/samples/{sampleId:guid}/deactivate", async (Guid id, Guid sampleId, EnrollmentReason request,
            AcademicAccess access, BackendDbContext db, GalleryPublisher gallery, CancellationToken ct) =>
        {
            await OwnEnrollment(id, access, db, ct);
            DomainException.Require(WireJson.Identifier(request.Reason), "reason_required");
            var sample = await db.Set<BiometricTemplate>().SingleOrDefaultAsync(t => t.BiometricEnrollmentId == id && t.BiometricTemplateId == sampleId, ct)
                ?? throw new DomainException("sample_not_found", 404);
            sample.BiometricTemplateActive = false;
            AuditLog.Add(db, access.User, "biometrics.deactivate_sample", sampleId.ToString(), request);
            return Results.Ok(await gallery.Publish(ct));
        }).RequireAuthorization(Roles.AdministratorPolicy);
        endpoints.MapPost("/admin/gallery-regeneration", async (GalleryPublisher gallery, CancellationToken ct) => Results.Ok(await gallery.Publish(ct)))
            .RequireAuthorization(Roles.AdministratorPolicy).AddEndpointFilter<AcademicMutationFilter>().WithTags("Biometrics");

        // Inference runs BEFORE acquiring the academic mutation lock, keeping Edge ingestion responsive.
        endpoints.MapPost("/biometric-enrollments/{id:guid}/samples", UploadSample).RequireAuthorization(Roles.StudentPolicy)
            .WithTags("Biometric enrollment").WithSummary("Extract a sample from a JPEG or PNG (base64, at most 5 MiB)");
        endpoints.MapPost("/biometric-enrollments/{id:guid}/samples/upload", UploadFile).RequireAuthorization(Roles.StudentPolicy)
            // Authentication uses an explicit bearer header, never ambient cookies.
            .DisableAntiforgery()
            // Keep photo buffers in memory, including rejected oversized parts.
            .WithFormOptions(memoryBufferThreshold: MaxImageBytes + 1, multipartBodyLengthLimit: MaxImageBytes,
                valueCountLimit: 2, keyLengthLimit: 64, valueLengthLimit: 64)
            .WithName("UploadEnrollmentPhoto").WithTags("Biometric enrollment")
            .WithSummary("Upload one enrollment photo (JPEG or PNG, at most 5 MiB)")
            .WithDescription("Sign in as the student and authorize Human. Choose a photo and its pose: frontal, left, right, up, or down. Upload 12 distinct photos, with at least two per pose; then submit the enrollment for administrator review. The backend extracts the embedding; the photo and filename are not retained.")
            .Produces<EnrollmentSampleResult>().Produces<ApiError>(400).Produces<ApiError>(409).Produces<ApiError>(413);
    }

    private static async Task<IResult> UploadFile(Guid id, [FromForm] string pose, IFormFile image, HttpRequest request,
        AcademicAccess access, BackendDbContext db, IEmbeddingExtractor extractor, IConfiguration config, CancellationToken ct)
    {
        DomainException.Require(request.Form.Files.Count == 1, "one_image_per_request");
        DomainException.Require(image.Length is > 0 and <= MaxImageBytes, "image_too_large");
        DomainException.Require(image.ContentType is "image/jpeg" or "image/png", "jpeg_or_png_required");
        var enrollment = await DraftEnrollment(id, pose, access, db, ct);
        using var stream = new MemoryStream((int)image.Length);
        await image.CopyToAsync(stream, ct);
        return await SaveSample(enrollment, pose, stream.ToArray(), access, db, extractor, config, ct);
    }

    private static async Task<IResult> UploadSample(Guid id, EnrollmentSample request, AcademicAccess access, BackendDbContext db,
        IEmbeddingExtractor extractor, IConfiguration config, CancellationToken ct)
    {
        var enrollment = await DraftEnrollment(id, request.Pose, access, db, ct);
        DomainException.Require(request.ImageBase64 is { Length: > 0 and <= 6990508 }, "invalid_sample");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.ImageBase64); } catch (FormatException) { throw new DomainException("invalid_image_encoding"); }
        DomainException.Require(bytes.Length is > 0 and <= MaxImageBytes, "image_too_large");
        return await SaveSample(enrollment, request.Pose, bytes, access, db, extractor, config, ct);
    }

    private static async Task<BiometricEnrollment> DraftEnrollment(Guid id, string pose, AcademicAccess access, BackendDbContext db, CancellationToken ct)
    {
        var enrollment = await OwnEnrollment(id, access, db, ct);
        DomainException.Require(enrollment.BiometricEnrollmentStatus == "draft", "enrollment_not_draft", 409);
        DomainException.Require(Poses.Contains(pose), "invalid_sample");
        return enrollment;
    }

    private static async Task<IResult> SaveSample(BiometricEnrollment enrollment, string pose, byte[] bytes, AcademicAccess access,
        BackendDbContext db, IEmbeddingExtractor extractor, IConfiguration config, CancellationToken ct)
    {
        var id = enrollment.BiometricEnrollmentId;
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var face = await extractor.Extract(bytes, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AcademicMutationFilter.Lock(db, ct);
        await db.Entry(enrollment).ReloadAsync(ct);
        DomainException.Require(enrollment.BiometricEnrollmentStatus == "draft", "enrollment_not_draft", 409);
        var student = await access.Student(ct);
        DomainException.Require(await db.Set<Account>().AnyAsync(a => a.AccountId == student.AccountId && a.AccountStatus == "approved", ct), "account_not_approved", 403);
        DomainException.Require(await db.Set<BiometricTemplate>().CountAsync(t => t.BiometricEnrollmentId == id, ct) < 12, "sample_limit_reached", 409);
        DomainException.Require(!await db.Set<BiometricTemplate>().AnyAsync(t => t.BiometricEnrollmentId == id && t.BiometricTemplateImageSha256 == hash, ct), "duplicate_sample", 409);
        var sample = new BiometricTemplate { BiometricEnrollmentId = id, BiometricTemplatePose = pose,
            BiometricTemplateModelSha256 = config["Biometrics:ModelSha256"]!, BiometricTemplateImageSha256 = hash,
            BiometricTemplateEmbedding = face.Embedding, BiometricTemplateDetectionScore = face.DetectionScore, BiometricTemplateAlignmentError = face.AlignmentError };
        db.Add(sample);
        AuditLog.Add(db, access.User, "biometrics.add_sample", sample.BiometricTemplateId.ToString(), new { enrollment_id = id, pose });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new EnrollmentSampleResult(sample.BiometricTemplateId, sample.BiometricTemplatePose,
            sample.BiometricTemplateDetectionScore, sample.BiometricTemplateAlignmentError));
    }
    private static async Task<BiometricEnrollment> OwnEnrollment(Guid id, AcademicAccess access, BackendDbContext db, CancellationToken ct)
    {
        var enrollment = await db.Set<BiometricEnrollment>().FindAsync([id], ct) ?? throw new DomainException("enrollment_not_found", 404);
        if (!access.IsAdmin) DomainException.Require(enrollment.StudentId == (await access.Student(ct)).StudentId, "own_enrollment_only", 403);
        return enrollment;
    }
}
public sealed record EnrollmentConsent(bool Accepted, string Version);
public sealed record EnrollmentSample(string Pose, string ImageBase64);
public sealed record EnrollmentSampleResult(Guid BiometricTemplateId, string BiometricTemplatePose, double BiometricTemplateDetectionScore, double BiometricTemplateAlignmentError);
public sealed record EnrollmentReview(string Decision, string Note, bool IdentityVerified);
public sealed record EnrollmentReason(string Reason);
