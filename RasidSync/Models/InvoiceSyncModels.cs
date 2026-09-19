namespace RasidSync.Models;

public sealed class InvoiceSyncPackage
{
    public Dictionary<string, object?> Header { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<Dictionary<string, object?>> Details { get; init; } = [];

    public List<Dictionary<string, object?>> Payments { get; init; } = [];

    public Dictionary<string, object?>? Due { get; init; }
}

public sealed class InvoiceQueueResult
{
    public long SyncId { get; init; }
    public long InvoiceId { get; init; }
    public string InvoiceUuid { get; init; } = string.Empty;
    public int DetailCount { get; init; }
    public int PaymentCount { get; init; }
    public bool HasDue { get; init; }
}
