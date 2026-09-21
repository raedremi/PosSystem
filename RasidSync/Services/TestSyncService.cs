using RasidSync.Models;

namespace RasidSync.Services;

public sealed class PendingSyncService
{
    private const int MaximumEventsPerRun = 100;

    public async Task<string> SendPendingAsync(SyncSettings settings)
    {
        var repository = new LocalSyncRepository(settings);
        var apiClient = new SyncApiClient(settings);
        await new SyncInfrastructureService(settings).EnsureCreatedAsync();

        int sentCount = 0;
        int blockedCount = 0;
        int processedCount = 0;
        long lastLocalSyncId = 0;
        long lastServerEventId = 0;

        while (processedCount < MaximumEventsPerRun)
        {
            SyncSendRow? row = await repository.GetNextEventAsync();
            if (row is null)
                break;

            processedCount++;

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
                // تعارض رقم الفاتورة يحتاج قرارًا يدويًا، لكنه لا يوقف بقية الفواتير.
                if (IsInvoiceNumberConflict(ex.Message))
                {
                    await repository.MarkBlockedAsync(
                        row,
                        "INVOICE_NUMBER_CONFLICT",
                        CleanServerError(ex.Message));
                    blockedCount++;
                    continue;
                }

                await repository.MarkFailedAsync(row.SyncId, ex.Message);
                throw new InvalidOperationException(
                    $"فشل إرسال الحركة {row.SyncId}. توقف الإرسال عندها وبقيت محفوظة لإعادة المحاولة. {ex.Message}",
                    ex);
            }
        }

        if (sentCount == 0 && blockedCount == 0)
            return "لا توجد حركات معلقة للإرسال.";

        string limitText = sentCount == MaximumEventsPerRun
            ? " تم إرسال الحد الأقصى لهذه الدفعة، ويمكن الضغط مرة أخرى لإكمال الباقي."
            : string.Empty;

        return $"تم إرسال {sentCount} حركة بنجاح، وتحويل {blockedCount} حركة إلى شاشة الأخطاء. " +
               $"آخر حركة محلية ناجحة {lastLocalSyncId}، ورقمها على السيرفر {lastServerEventId}.{limitText}";
    }

    private static bool IsInvoiceNumberConflict(string message) =>
        message.Contains("مستخدم لفاتورة أخرى", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("INVOICE_NUMBER_CONFLICT", StringComparison.OrdinalIgnoreCase);

    private static string CleanServerError(string message)
    {
        string prefix = "تعارض رقم الفاتورة: الرقم مستخدم لفاتورة أخرى ذات UUID مختلف. " +
                        "لم يتم تعديل أية بيانات.";
        return message.Contains("ExistingUuid=", StringComparison.OrdinalIgnoreCase)
            ? prefix + Environment.NewLine + message
            : prefix;
    }
}
