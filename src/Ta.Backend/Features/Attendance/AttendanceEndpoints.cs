using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ta.Backend.Common;
using Ta.Backend.Features.Devices;
using Ta.Backend.Persistence;

namespace Ta.Backend.Features.Attendance;

public static class AttendanceEndpoints
{
    private static readonly string[] RequiredFields = ["schema_version", "event_id", "occurred_at", "device_id",
        "identity_id", "gallery_version", "runtime_version", "track_id", "camera_frame_id", "similarity",
        "similarity_margin", "pad_median_p_real"];

    public static void MapAttendanceEndpoints(this IEndpointRouteBuilder endpoints) => endpoints
        .MapPost("/attendance-events/batch", Ingest).RequireAuthorization(Credentials.DeviceScheme)
        .WithTags("Attendance").WithName("IngestAttendanceBatch")
        .WithSummary("Ingest an attendance event batch")
        .WithDescription("Accepts 1-256 events (at most 1 MiB). Returns accepted, duplicate, or rejected per event after commit. Track and frame IDs are decimal strings. Invalid envelopes return 400; individual invalid events receive rejected receipts.")
        .Produces<AttendanceBatchReceipt>().Produces<ApiError>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status413PayloadTooLarge);

    private static async Task<IResult> Ingest(AttendanceEnvelope batch, HttpContext context, BackendDbContext db, AttendanceEvaluator evaluator, CancellationToken ct)
    {
        if (batch.DeviceId != context.User.FindFirstValue(ClaimTypes.NameIdentifier)) return Results.Forbid();
        if (batch.SchemaVersion != 1 || batch.Events is null || batch.Events.Length is < 1 or > 256)
            return Results.BadRequest(new { error = "invalid_batch_envelope" });

        // Every receipt must have a unique ID from the submitted batch or Edge rejects the ACK.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in batch.Events)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("event_id", out var id)
                || id.ValueKind != JsonValueKind.String || !WireJson.Identifier(id.GetString()) || !ids.Add(id.GetString()!))
                return Results.BadRequest(new { error = "invalid_or_repeated_event_id" });
        }

        var receipts = new List<EventReceipt>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AcademicMutationFilter.Lock(db, ct);
        // Sorting locks avoids deadlocks for concurrent overlapping batches. ACK order is irrelevant to Edge.
        foreach (var item in batch.Events.OrderBy(e => LockOrder(e.GetProperty("event_id").GetString()!), StringComparer.Ordinal))
        {
            var id = item.GetProperty("event_id").GetString()!;
            AttendanceObservation? observation = null;
            try
            {
                if (RequiredFields.All(f => item.TryGetProperty(f, out _))
                    && item.EnumerateObject().Count() == RequiredFields.Length)
                    observation = item.Deserialize<AttendanceObservation>(WireJson.Options);
            }
            catch (JsonException) { }
            if (observation is null || !observation.IsValid() || observation.DeviceId != batch.DeviceId)
            {
                receipts.Add(new(id, "rejected", "invalid_event"));
                continue;
            }

            // Normalize known schema fields, dates and GUID spelling before hashing. JSON formatting
            // and object property order do not change event identity. Decimal uint64 strings stay strings.
            var eventId = Guid.Parse(observation.EventId);
            observation = observation with { EventId = eventId.ToString("D"), OccurredAt = observation.OccurredAt.ToUniversalTime() };
            var payload = JsonSerializer.Serialize(observation, WireJson.Options);
            var hash = Credentials.Hash(payload);
            var receivedAt = DateTimeOffset.UtcNow;
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO attendance_event
                (attendance_event_id, device_id, attendance_identity_id, attendance_gallery_version,
                 attendance_occurred_at, attendance_received_at, attendance_payload, attendance_payload_hash)
                VALUES ({eventId}, {batch.DeviceId}, {observation.IdentityId}, {observation.GalleryVersion},
                        {observation.OccurredAt}, {receivedAt}, CAST({payload} AS jsonb), {hash})
                ON CONFLICT (attendance_event_id) DO NOTHING
                """, ct);
            if (inserted == 1)
            {
                await evaluator.Evaluate(eventId, observation, ct);
                await db.SaveChangesAsync(ct);
                receipts.Add(new(id, "accepted"));
            }
            else
            {
                var existing = await db.AttendanceEvents.AsNoTracking().SingleAsync(e => e.AttendanceEventId == eventId, ct);
                var same = existing.DeviceId == batch.DeviceId && existing.AttendancePayloadHash == hash;
                receipts.Add(new(id, same ? "duplicate" : "rejected", same ? null : "event_id_conflict"));
            }
        }
        // Never acknowledge before COMMIT: an uncertain response can safely be retried.
        await transaction.CommitAsync(ct);
        return Results.Ok(new AttendanceBatchReceipt(1, receipts));
    }

    private static string LockOrder(string id) => Guid.TryParseExact(id, "D", out var parsed) ? parsed.ToString("D") : id;
}
