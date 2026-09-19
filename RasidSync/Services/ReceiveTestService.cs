using RasidSync.Models;

namespace RasidSync.Services;

public sealed class ReceiveTestService
{
    public async Task<string> PullAndSaveAsync(SyncSettings settings)
    {
        var repository = new ReceiveSyncRepository(settings);
        var apiClient = new PullApiClient(settings);

        long lastReceivedId = await repository.GetLastReceivedIdAsync();
        PullSyncResponse response = await apiClient.PullAsync(lastReceivedId);

        await repository.SaveEventsAndCursorAsync(
            response.Events,
            response.NextCursor);

        if (response.Events.Count == 0)
        {
            return response.NextCursor > lastReceivedId
                ? $"لا توجد حركات لجهازك، وتم تحريك المؤشر إلى {response.NextCursor}."
                : $"لا توجد حركات جديدة بعد الرقم {lastReceivedId}.";
        }

        return $"تم استقبال وحفظ {response.Events.Count} حركة. أصبح LastReceivedId = {response.NextCursor}.";
    }
}
