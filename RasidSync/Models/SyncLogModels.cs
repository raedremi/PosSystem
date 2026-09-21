namespace RasidSync.Models;

public sealed class SyncLogRow
{
    public long Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityUuid { get; set; } = string.Empty;
    public long? LocalId { get; set; }
    public int OperationType { get; set; }
    public int Status { get; set; }
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class SyncErrorRow
{
    public long ErrorId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public long RelatedId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityUuid { get; set; } = string.Empty;
    public long? LocalId { get; set; }
    public int OperationType { get; set; }
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string? ErrorDetails { get; set; }
    public int ResolutionStatus { get; set; }
    public DateTime CreatedAt { get; set; }
}
