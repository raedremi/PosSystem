using RasidSync.Models;

namespace RasidSync.Services;

/// <summary>
/// ينزّل الحركات ويحفظها ثم يطبقها بالترتيب.
/// خطأ فاتورة واحدة يعزل ويُستكمل ما بعدها، أما عطل الاتصال العام فيوقف الدورة.
/// </summary>
public sealed class ReceiveSyncService
{
    public async Task<string> PullAndApplyAsync(SyncSettings settings)
    {
        // الجهاز الموقوف لا ينزّل الأحداث ولا يحرّك LastReceivedId.
        await new DeviceAuthorizationClient(settings).EnsureAllowedAsync();

        var repository = new ReceiveSyncRepository(settings);
        var apiClient = new PullApiClient(settings);
        var invoiceService = new LocalInvoiceApplyService(settings);
        await new SyncInfrastructureService(settings).EnsureCreatedAsync();

        long cursor = await repository.GetLastReceivedIdAsync();
        PullSyncResponse response = await apiClient.PullAsync(cursor);
        await repository.SaveEventsAsync(response.Events);

        int applied = 0;
        int skipped = 0;
        int blocked = 0;
        foreach (PulledSyncEvent item in response.Events.OrderBy(x => x.ServerEventId))
        {
            int? status = await repository.GetApplyStatusAsync(item.ServerEventId);
            if (status == 2)
            {
                await repository.AdvanceCursorAsync(item.ServerEventId);
                cursor = item.ServerEventId;
                continue;
            }

            // لا نطبق حركة لاحقة لنفس الفاتورة إذا كانت لها حركة أقدم تحتاج قرارًا يدويًا.
            if (await repository.HasBlockedEntityAsync(item.EntityUuid, item.ServerEventId))
            {
                await repository.MarkBlockedAndAdvanceAsync(
                    item,
                    "ENTITY_HAS_BLOCKED_EVENT",
                    "توجد حركة أقدم لنفس UUID تحتاج معالجة قبل هذه الحركة.");
                cursor = item.ServerEventId;
                blocked++;
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
                string errorCode = SyncFailureClassifier.GetErrorCode(ex);
                string errorMessage = SyncFailureClassifier.GetFullMessage(ex);

                // إذا انقطع الاتصال أو قاعدة البيانات غير متاحة نتوقف؛ المشكلة عامة.
                if (SyncFailureClassifier.IsInfrastructureFailure(ex))
                {
                    await repository.MarkFailedAsync(item, errorCode, errorMessage);
                    throw new InvalidOperationException(
                        $"توقفت دورة الاستقبال بسبب مشكلة اتصال أو قاعدة بيانات عند حركة السيرفر " +
                        $"{item.ServerEventId}. سنعيد المحاولة لاحقًا: {errorMessage}", ex);
                }

                // الخطأ خاص بهذه الفاتورة؛ نسجله ونتقدم حتى لا تتوقف بقية الفواتير.
                if (errorCode == "INVOICE_NUMBER_CONFLICT")
                    errorMessage = "تعارض رقم الفاتورة مع فاتورة محلية تحمل UUID مختلفًا. " +
                                   "لم يتم تعديل أية بيانات." + Environment.NewLine + errorMessage;

                await repository.MarkBlockedAndAdvanceAsync(item, errorCode, errorMessage);
                cursor = item.ServerEventId;
                blocked++;
                continue;
            }
        }

        // nextCursor قد يتجاوز آخر حدث مستلم لأن السيرفر يستبعد حركات هذا الجهاز.
        if (response.NextCursor > cursor)
            await repository.AdvanceCursorAsync(response.NextCursor);

        return response.Events.Count == 0
            ? $"لا توجد حركات جديدة. المؤشر الحالي {Math.Max(cursor, response.NextCursor)}."
            : $"تم استقبال {response.Events.Count} حركة، تطبيق {applied} فاتورة، تحويل {blocked} حركة للأخطاء، وتجاوز {skipped} حركة غير مدعومة. المؤشر {Math.Max(cursor, response.NextCursor)}.";
    }
}
