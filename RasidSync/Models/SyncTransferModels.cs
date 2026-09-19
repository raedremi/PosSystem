using System.Text.Json;

namespace RasidSync.Models;

public sealed class SyncSendRow
{
    public long SyncId { get; init; }
    public string EventUuid { get; init; } = string.Empty;
    public string SourceDeviceUuid { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string EntityUuid { get; init; } = string.Empty;
    public long? LocalId { get; init; }
    public int OperationType { get; init; }
    public string Payload { get; init; } = "{}";
    public DateTime CreatedAt { get; init; }
}

public sealed class PushSyncRequest
{
    public string EventUuid { get; init; } = string.Empty;
    public string SourceDeviceUuid { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string EntityUuid { get; init; } = string.Empty;
    public long? LocalId { get; init; }
    public int OperationType { get; init; }
    public JsonElement Payload { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed class PushSyncResponse
{
    public bool Success { get; init; }
    public bool AlreadyProcessed { get; init; }
    public long ServerEventId { get; init; }
    public string Message { get; init; } = string.Empty;
}
