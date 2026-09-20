namespace Ta.Backend.Features.Devices;

public sealed class Device
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DeviceTokenHash { get; set; } = "";
    public bool DeviceEnabled { get; set; } = true;
    public DateTimeOffset DeviceCreatedAt { get; set; }
    public DateTimeOffset DeviceCredentialChangedAt { get; set; }
}
