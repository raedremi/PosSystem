using RasidSync.Models;

namespace RasidSync.Services;

/// <summary>
/// ينزّل الحركات ويحفظها ثم يطبقها بالترتيب. يتوقف عند أول خطأ.
/// </summary>
public sealed class ReceiveSyncService
{
    public async Task<string> PullAndApplyAsync(SyncSettings settings)
    {
        var repository = new ReceiveSyncRepository(settings);
        var apiClient = new PullApiClient(settings);
        var invoiceService = new LocalInvoiceApplyService(settings);

        long cursor = await repository.GetLastReceivedIdAsync();
        PullSyncResponse response = await apiClient.PullAsync(cursor);
        await repository.SaveEventsAsync(response.Events);

        int applied = 0;
        int skipped = 0;
        foreach (PulledSyncEvent item in response.Events.OrderBy(x => x.ServerEventId))
        {
            int? status = await repository.GetApplyStatusAsync(item.ServerEventId);
            if (status == 2)
            {
                await repository.AdvanceCursorAsync(item.ServerEventId);
                cursor = item.ServerEventId;
                continue;
            }

            await repository.MarkApplyingAsync(item.ServerEventId);
            try
            {
                if (string.Equals(item.EntityType, "Invoice", StringComparison.OrdinalIgnoreCase))
                {
                    await invoiceService.ApplyAsync(item);
                    applied++;
                }
                else
                {
                    // الحركات التجريبية أو الأنواع التي سنضيفها لاحقًا لا توقف طابور الفواتير.
                    skipped++;
                }

                await repository.MarkAppliedAndAdvanceAsync(item.ServerEventId);
                cursor = item.ServerEventId;
            }
            catch (Exception ex)
            {
                await repository.MarkFailedAsync(item.ServerEventId, ex.ToString());
                throw new InvalidOperationException(
                    $"فشل تطبيق حركة السيرفر {item.ServerEventId}. تم حفظ الخطأ وسنبدأ منها في المحاولة القادمة: {ex.Message}", ex);
            }
        }

        // nextCursor قد يتجاوز آخر حدث مستلم لأن السيرفر يستبعد حركات هذا الجهاز.
        if (response.NextCursor > cursor)
            await repository.AdvanceCursorAsync(response.NextCursor);

        return response.Events.Count == 0
            ? $"لا توجد حركات جديدة. المؤشر الحالي {Math.Max(cursor, response.NextCursor)}."
            : $"تم استقبال {response.Events.Count} حركة، تطبيق {applied} فاتورة وتجاوز {skipped} حركة غير مدعومة. المؤشر {Math.Max(cursor, response.NextCursor)}.";
    }
}
