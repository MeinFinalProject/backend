using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.AcademicManagement;
using Ta.Backend.Features.Devices;
using Ta.Backend.Features.Identity;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Biometrics;

public sealed class GalleryPublisher(BackendDbContext db, IConfiguration configuration)
{
    // Caller owns the academic transaction. Flush template changes before constructing the snapshot.
    public async Task<GalleryPublication> Publish(CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(741829301)", ct);
        var modelHash = configuration["Biometrics:ModelSha256"]!;
        var templates = await (from t in db.Set<BiometricTemplate>() join e in db.Set<BiometricEnrollment>() on t.BiometricEnrollmentId equals e.BiometricEnrollmentId
            join s in db.Set<Student>() on e.StudentId equals s.StudentId join a in db.Set<Account>() on s.AccountId equals a.AccountId
            where t.BiometricTemplateActive && e.BiometricEnrollmentStatus == "approved" && a.AccountStatus == "approved" && t.BiometricTemplateModelSha256 == modelHash
            orderby t.BiometricTemplateId select new { t.BiometricTemplateId, s.StudentIdentityId, t.BiometricTemplateEmbedding }).Take(10001).ToListAsync(ct);
        DomainException.Require(templates.Count <= 10000, "gallery_exceeds_edge_limit", 409);
        var version = "enrollment-" + Guid.NewGuid().ToString("N");
        var document = new GalleryDocument(1, version, "insightface/w600k_r50", modelHash, 512, "f32le-base64",
            templates.Select(t => new GalleryTemplate(t.BiometricTemplateId.ToString("D"), t.StudentIdentityId, 512, "f32le-base64", Convert.ToBase64String(t.BiometricTemplateEmbedding))).ToArray());
        var json = JsonSerializer.Serialize(document, WireJson.Options);
        DomainException.Require(Encoding.UTF8.GetByteCount(json) <= 32 * 1024 * 1024, "gallery_exceeds_edge_limit", 409);
        var etag = '"' + Credentials.Hash(json) + '"';
        db.Add(new GalleryRelease { GalleryVersion = version, GalleryModelSha256 = modelHash, GalleryDocument = json,
            GalleryEtag = etag, GalleryTemplateCount = templates.Count, GalleryPublishedAt = DateTimeOffset.UtcNow });
        return new(version, etag, "published");
    }
}
