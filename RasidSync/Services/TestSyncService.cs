using RasidSync.Models;

namespace RasidSync.Services;

public sealed class TestSyncService
{
    public async Task<string> CreateAndSendAsync(SyncSettings settings)
    {
        var repository = new LocalSyncRepository(settings);
        var apiClient = new SyncApiClient(settings);

        await repository.CreateTestEventAsync();
        SyncSendRow? row = await repository.GetNextEventAsync();

        if (row is null)
            throw new InvalidOperationException("تم إنشاء الحركة لكن تعذر قراءتها من جدول الإرسال.");

        try
        {
            PushSyncResponse response = await apiClient.PushAsync(row);
            await repository.MarkSentAsync(row.SyncId);

            return $"نجح إرسال الحركة المحلية {row.SyncId}، ورقمها على السيرفر {response.ServerEventId}.";
        }
        catch (Exception ex)
        {
            await repository.MarkFailedAsync(row.SyncId, ex.Message);
            throw new InvalidOperationException(
                $"فشل إرسال الحركة {row.SyncId}. بقيت محفوظة لإعادة المحاولة. {ex.Message}",
                ex);
        }
    }
}
