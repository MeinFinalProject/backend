namespace Ta.Backend.Features.Attendance;

// An immutable observation, not a final academic attendance decision. Identity/gallery
// references may predate backend enrollment, so they deliberately remain external IDs.
public sealed class AttendanceEvent
{
    public Guid AttendanceEventId { get; set; }
    public string DeviceId { get; set; } = "";
    public string AttendanceIdentityId { get; set; } = "";
    public string AttendanceGalleryVersion { get; set; } = "";
    public DateTimeOffset AttendanceOccurredAt { get; set; }
    public DateTimeOffset AttendanceReceivedAt { get; set; }
    public string AttendancePayload { get; set; } = "";
    public string AttendancePayloadHash { get; set; } = "";
}
