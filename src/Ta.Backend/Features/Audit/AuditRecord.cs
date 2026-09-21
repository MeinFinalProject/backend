using System.Security.Claims;
using System.Text.Json;
using Ta.Backend.Common;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Audit;

public sealed class AuditRecord
{
    public Guid AuditRecordId { get; set; } = Guid.NewGuid();
    public string AuditActor { get; set; } = "";
    public string AuditAction { get; set; } = "";
    public string AuditResource { get; set; } = "";
    public string AuditDetails { get; set; } = "{}";
    public DateTimeOffset AuditOccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class AuditLog
{
    public static void Add(BackendDbContext db, ClaimsPrincipal actor, string action, string resource, object? details = null) =>
        db.Set<AuditRecord>().Add(new AuditRecord
        {
            AuditActor = actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
            AuditAction = action, AuditResource = resource,
            AuditDetails = JsonSerializer.Serialize(details ?? new { }, WireJson.Options)
        });
}
