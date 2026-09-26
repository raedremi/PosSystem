using RasidSync.Models;

namespace RasidSync.Services;

public sealed class PendingSyncService
{
    private const int MaximumEventsPerRun = 100;

    public async Task<string> SendPendingAsync(SyncSettings settings)
    {
        // لا نقرأ أو نرسل أية حركة قبل موافقة السيرفر على الجهاز.
        await new DeviceAuthorizationClient(settings).EnsureAllowedAsync();

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
                string errorCode = SyncFailureClassifier.GetErrorCode(ex);
                string errorMessage = CleanServerError(
                    SyncFailureClassifier.GetFullMessage(ex), errorCode);

                // انقطاع الاتصال أو عطل السيرفر مشكلة عامة؛ نوقف الدورة ونحاول لاحقًا.
                if (SyncFailureClassifier.IsInfrastructureFailure(ex))
                {
                    await repository.MarkFailedAsync(row, errorCode, errorMessage);
                    throw new InvalidOperationException(
                        $"توقفت دورة الإرسال بسبب مشكلة اتصال أو سيرفر عند الحركة {row.SyncId}. " +
                        "بقيت الحركة محفوظة لإعادة المحاولة. " + errorMessage,
                        ex);
                }

                // خطأ خاص بهذه الفاتورة: نعزلها ونكمل الفواتير الأخرى.
                await repository.MarkBlockedAsync(row, errorCode, errorMessage);
                blockedCount++;
                continue;
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

    private static string CleanServerError(string message, string errorCode)
    {
        if (errorCode != "INVOICE_NUMBER_CONFLICT")
            return message;

        string prefix = "تعارض رقم الفاتورة: الرقم مستخدم لفاتورة أخرى ذات UUID مختلف. " +
                        "لم يتم تعديل أية بيانات.";
        return message.Contains("ExistingUuid=", StringComparison.OrdinalIgnoreCase)
            ? prefix + Environment.NewLine + message
            : prefix;
    }
}
