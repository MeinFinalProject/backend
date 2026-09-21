using System.Text.Json.Serialization;

namespace Ta.Backend.Features.Devices;

// One latest observation per device. Connection liveness is owned by this server process.
public sealed class DeviceOperationalStatus
{
    public string DeviceId { get; set; } = "";
    public DateTimeOffset DeviceLastSeenAt { get; set; }
    public string DeviceRuntimeState { get; set; } = "starting";
    public long? DeviceFrameAgeMs { get; set; }
    public string DeviceInstalledGalleryVersion { get; set; } = "";
    public string DeviceModelSha256 { get; set; } = "";
    public int DeviceInstalledTemplateCount { get; set; }
    public long DeviceOutboxPendingCount { get; set; }
    public long DeviceOutboxDeadCount { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeviceTelemetry(int SchemaVersion, string Type, string RuntimeState, long? FrameAgeMs,
    string InstalledGalleryVersion, string ModelSha256, int InstalledTemplateCount,
    long OutboxPendingCount, long OutboxDeadCount)
{
    public bool IsValid() => SchemaVersion == 1 && Type == "status"
        && RuntimeState is "starting" or "running" or "persistence_blocked" or "gallery_error" or "stopping"
        && FrameAgeMs is null or >= 0 and <= 86_400_000
        && InstalledGalleryVersion is { Length: <= 256 } && !InstalledGalleryVersion.Any(char.IsControl)
        && ModelSha256 is { Length: 64 } && ModelSha256.All(char.IsAsciiHexDigitLower)
        && InstalledTemplateCount is >= 0 and <= 10000 && OutboxPendingCount >= 0 && OutboxDeadCount >= 0;
}

public sealed record DeviceOperationalView(string DeviceId, bool Connected, bool Healthy, bool AttendanceReady,
    IReadOnlyList<string> Reasons, string? ExpectedGalleryVersion, DeviceOperationalStatus? Latest);
