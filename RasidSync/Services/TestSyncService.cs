using RasidSync.Models;

namespace RasidSync.Services;

public sealed class PendingSyncService
{
    private const int MaximumEventsPerRun = 100;

    public async Task<string> SendPendingAsync(SyncSettings settings)
    {
        var repository = new LocalSyncRepository(settings);
        var apiClient = new SyncApiClient(settings);

        int sentCount = 0;
        long lastLocalSyncId = 0;
        long lastServerEventId = 0;

        while (sentCount < MaximumEventsPerRun)
        {
            SyncSendRow? row = await repository.GetNextEventAsync();
            if (row is null)
                break;

            try
            {
                PushSyncResponse response = await apiClient.PushAsync(row);
                await repository.MarkSentAsync(row.SyncId);

                sentCount++;
                lastLocalSyncId = row.SyncId;
                lastServerEventId = response.ServerEventId;
            }
            catch (Exception ex)
            {
                await repository.MarkFailedAsync(row.SyncId, ex.Message);
                throw new InvalidOperationException(
                    $"فشل إرسال الحركة {row.SyncId}. توقف الإرسال عندها وبقيت محفوظة لإعادة المحاولة. {ex.Message}",
                    ex);
            }
        }

        if (sentCount == 0)
            return "لا توجد حركات معلقة للإرسال.";

        string limitText = sentCount == MaximumEventsPerRun
            ? " تم إرسال الحد الأقصى لهذه الدفعة، ويمكن الضغط مرة أخرى لإكمال الباقي."
            : string.Empty;

        return $"تم إرسال {sentCount} حركة بنجاح. آخر حركة محلية {lastLocalSyncId}، ورقمها على السيرفر {lastServerEventId}.{limitText}";
    }
}
