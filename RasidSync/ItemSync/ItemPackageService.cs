using RasidSync.CustomerSupplierSync;
using RasidSync.Models;
using Rasid.ItemSync;

namespace RasidSync.ItemSync;

/// <summary>استكمال الحفظ الجزئي وتجهيز الاستبدال من البطاقة المحلية الحالية.</summary>
public sealed class ItemPackageService
{
    private readonly SyncSettings settings;
    public ItemPackageService(SyncSettings value) => settings = value;
    public async Task RecoverAsync()
    {
        await using var db = CustomerPackageService.CreateConnection(settings);
        await db.OpenAsync();
        new ItemSyncStore(db).Recover();
    }
    public async Task<long> BuildAndQueueAsync(long itemId, string expectedUuid)
    {
        if (!ItemSyncContract.Uuid(itemId).Equals(expectedUuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("هوية الصنف لا تطابق سجل المزامنة.");
        await using var db = CustomerPackageService.CreateConnection(settings);
        await db.OpenAsync();
        var store = new ItemSyncStore(db);
        using (store.Lock())
        {
            store.Recover();
            string eventId = Guid.NewGuid().ToString();
            store.Apply(eventId, expectedUuid, 4, store.FullPackage(itemId).ToString(), true, settings.DeviceUuid);
            return Convert.ToInt64(store.Scalar("SELECT sync_id FROM tbl_sync_send WHERE event_uuid=@p0", eventId));
        }
    }
}
