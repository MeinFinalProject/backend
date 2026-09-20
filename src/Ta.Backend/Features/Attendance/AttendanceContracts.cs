using System.Globalization;
using System.Text.Json;
using Ta.Backend.Common;

namespace Ta.Backend.Features.Attendance;

public sealed record AttendanceEnvelope(int SchemaVersion, string DeviceId, JsonElement[] Events);
public sealed record AttendanceObservation(
    int SchemaVersion, string EventId, DateTimeOffset OccurredAt, string DeviceId,
    string IdentityId, string GalleryVersion, string RuntimeVersion, string TrackId,
    string CameraFrameId, double Similarity, double SimilarityMargin, double PadMedianPReal)
{
    public bool IsValid() => SchemaVersion == 1 && Guid.TryParseExact(EventId, "D", out _)
        && OccurredAt > DateTimeOffset.UnixEpoch && OccurredAt <= DateTimeOffset.UtcNow.AddMinutes(10)
        && WireJson.Identifier(DeviceId) && WireJson.Identifier(IdentityId)
        && WireJson.Identifier(GalleryVersion) && WireJson.Identifier(RuntimeVersion)
        && IsUInt64(TrackId) && IsUInt64(CameraFrameId)
        && double.IsFinite(Similarity) && Similarity is >= -1.00001 and <= 1.00001
        && double.IsFinite(SimilarityMargin) && SimilarityMargin is >= 0 and <= 2.00001
        && double.IsFinite(PadMedianPReal) && PadMedianPReal is >= 0 and <= 1;

    private static bool IsUInt64(string? value) => value is { Length: > 0 and <= 20 }
        && value.All(char.IsAsciiDigit) && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
}
public sealed record EventReceipt(string EventId, string Status, string? Reason = null);
public sealed record AttendanceBatchReceipt(int SchemaVersion, IReadOnlyList<EventReceipt> Results);
