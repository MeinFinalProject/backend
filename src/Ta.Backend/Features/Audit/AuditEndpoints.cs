using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.Identity;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Audit;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Keep the existing array/offset contract; filters apply before pagination.
        endpoints.MapGet("/admin/audit-records", async (int? offset, string? action, string? actor,
            string? resource, DateTimeOffset? from, DateTimeOffset? until, BackendDbContext db, CancellationToken ct) =>
        {
            DomainException.Require((offset ?? 0) >= 0 && (action?.Length ?? 0) <= 100
                && (actor?.Length ?? 0) <= 200 && (resource?.Length ?? 0) <= 500
                && (from == null || until == null || from < until), "invalid_audit_filter");
            var query = db.Set<AuditRecord>().AsNoTracking();
            if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.AuditAction == action);
            if (!string.IsNullOrWhiteSpace(actor)) query = query.Where(a => a.AuditActor == actor);
            if (!string.IsNullOrWhiteSpace(resource)) query = query.Where(a => a.AuditResource == resource);
            if (from.HasValue) { var utc = from.Value.ToUniversalTime(); query = query.Where(a => a.AuditOccurredAt >= utc); }
            if (until.HasValue) { var utc = until.Value.ToUniversalTime(); query = query.Where(a => a.AuditOccurredAt < utc); }
            var records = await query.OrderByDescending(a => a.AuditOccurredAt).ThenBy(a => a.AuditRecordId)
                .Skip(offset ?? 0).Take(100).ToListAsync(ct);
            var actorIds = records.Select(a => Guid.TryParse(a.AuditActor, out var id) ? id : Guid.Empty).Distinct().ToArray();
            var names = await db.Set<Account>().Where(a => actorIds.Contains(a.AccountId))
                .ToDictionaryAsync(a => a.AccountId, a => a.AccountName, ct);
            return Results.Ok(records.Select(a => new { a.AuditRecordId, a.AuditActor, a.AuditAction,
                a.AuditResource, a.AuditDetails, a.AuditOccurredAt,
                actor_name = Guid.TryParse(a.AuditActor, out var id) ? names.GetValueOrDefault(id) : null }));
        }).RequireAuthorization(Roles.AdministratorPolicy).WithTags("Audit")
            .WithSummary("Read audit records with exact action, actor, resource and UTC time filters");
    }
}
