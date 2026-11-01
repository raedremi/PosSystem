using RasidSync.CustomerSupplierSync;
using RasidSync.Models;
using Rasid.ItemSync;

namespace RasidSync.ItemSync;

/// <summary>الاستقبال المحلي يستخدم المحرك نفسه الموجود في API ورصيد.</summary>
public sealed class LocalItemApplyService
{
    private readonly SyncSettings settings;
    public LocalItemApplyService(SyncSettings value) => settings = value;
    public async Task<string> ApplyAsync(PulledSyncEvent item)
    {
        await using var connection = CustomerPackageService.CreateConnection(settings);
        await connection.OpenAsync();
        return new ItemSyncStore(connection).Apply(item.EventUuid, item.EntityUuid, item.OperationType, item.Payload.GetRawText());
    }
}
