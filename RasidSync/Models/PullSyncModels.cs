using System.Text.Json;

namespace RasidSync.Models;

public sealed class PullSyncResponse
{
    public List<PulledSyncEvent> Events { get; init; } = [];
    public long NextCursor { get; init; }
    public bool HasMore { get; init; }
}

public sealed class PulledSyncEvent
{
    public long ServerEventId { get; init; }
    public string EventUuid { get; init; } = string.Empty;
    public string SourceDeviceUuid { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string EntityUuid { get; init; } = string.Empty;
    public long? LocalId { get; init; }
    public int OperationType { get; init; }
    public JsonElement Payload { get; init; }
    public DateTime CreatedAt { get; init; }
}
