namespace RasidSync.Models;

public sealed class SyncDeviceCheckRequest
{
    public string DeviceUuid { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string AppVersion { get; init; } = string.Empty;
}

public sealed class SyncDeviceCheckResponse
{
    public bool Allowed { get; init; }
    public bool NewlyRegistered { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? MinimumVersion { get; init; }
}
