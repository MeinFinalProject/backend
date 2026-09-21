using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Ta.Backend.Common;
using Ta.Backend.Features.Devices;
using Ta.Backend.Features.Audit;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Biometrics;

public static class GalleryEndpoints
{
    public static void MapGalleryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/gallery", async (HttpContext context, BackendDbContext db, CancellationToken ct) =>
        {
            var gallery = await db.GalleryReleases.AsNoTracking().OrderByDescending(g => g.GalleryReleaseId).FirstOrDefaultAsync(ct);
            context.Response.Headers.CacheControl = "private, no-cache";
            context.Response.Headers.Vary = "Authorization";
            if (gallery is null)
            {
                context.Response.Headers.RetryAfter = "60";
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            context.Response.Headers.ETag = gallery.GalleryEtag;
            if (EntityTagHeaderValue.TryParseList(context.Request.Headers.IfNoneMatch.OfType<string>().ToArray(), out var tags)
                && tags.Any(t => t == EntityTagHeaderValue.Any || t.Compare(new EntityTagHeaderValue(gallery.GalleryEtag), false)))
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Text(gallery.GalleryDocument, "application/json", Encoding.UTF8);
        }).RequireAuthorization(Credentials.DeviceScheme).WithTags("Biometrics")
            .WithName("GetGallery").WithSummary("Download the current biometric gallery")
            .WithDescription("Send the previous ETag in If-None-Match to receive 304 when the gallery is unchanged. A 200 response includes the current ETag.")
            .Produces<GalleryDocument>().Produces(StatusCodes.Status304NotModified);

        endpoints.MapPost("/admin/gallery-releases", async (GalleryDocument document, IConfiguration configuration,
            BackendDbContext db, HttpContext context, CancellationToken ct) =>
        {
            var error = document.Validate(configuration["Biometrics:ModelSha256"]!);
            if (error is not null) return Results.BadRequest(new { error });
            document = document with { Templates = document.Templates.OrderBy(t => t.TemplateId, StringComparer.Ordinal).ToArray() };
            var json = JsonSerializer.Serialize(document, WireJson.Options);
            if (Encoding.UTF8.GetByteCount(json) > 32 * 1024 * 1024)
                return Results.BadRequest(new { error = "gallery_exceeds_edge_limit" });
            var etag = '"' + Credentials.Hash(json) + '"';
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(741829301)", ct);
            var existing = await db.GalleryReleases.SingleOrDefaultAsync(g => g.GalleryVersion == document.GalleryVersion, ct);
            if (existing is not null)
                return existing.GalleryEtag == etag
                    ? Results.Ok(new GalleryPublication(document.GalleryVersion, etag, "duplicate"))
                    : Results.Conflict(new { error = "immutable_gallery_version" });
            db.GalleryReleases.Add(new GalleryRelease
            {
                GalleryVersion = document.GalleryVersion,
                GalleryModelSha256 = document.ModelSha256,
                GalleryDocument = json,
                GalleryEtag = etag,
                GalleryTemplateCount = document.Templates.Length,
                GalleryPublishedAt = DateTimeOffset.UtcNow
            });
            AuditLog.Add(db, context.User, "gallery.publish_document", document.GalleryVersion, new { templates = document.Templates.Length });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Created($"{ApiRoutes.V1}/gallery", new GalleryPublication(document.GalleryVersion, etag, "published"));
        }).RequireAuthorization(Credentials.AdminScheme).WithTags("Biometrics")
            .WithName("PublishGallery").WithSummary("Publish an immutable gallery release")
            .WithDescription("Each template contains 512 finite float32 little-endian values encoded as base64. Publishing an empty templates array revokes the previous gallery.")
            .Produces<GalleryPublication>(StatusCodes.Status201Created).Produces<GalleryPublication>()
            .Produces<ApiError>(StatusCodes.Status400BadRequest).Produces<ApiError>(StatusCodes.Status409Conflict);
    }
}

public sealed record GalleryPublication(string GalleryVersion, string Etag, string Status);
