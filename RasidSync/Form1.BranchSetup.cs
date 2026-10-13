using RasidSync.Services;

namespace RasidSync;

public partial class Form1
{
    private void AddBranchSetupButton()
    {
        var button = CreateSyncActionButton("تهيئة فرع جديد", Color.FromArgb(110, 95, 70));
        button.Click += async (_, _) => await SetupNewBranchAsync(button);
        _actionsPanel.Controls.Add(button);
    }

    private async Task SetupNewBranchAsync(Button button)
    {
        if (_syncOperationRunning)
        {
            SetStatus("انتظر انتهاء عملية المزامنة الحالية.", false);
            return;
        }
        _syncOperationRunning = true;
        bool timerWasRunning = _syncTimer.Enabled;
        _syncTimer.Stop();
        SetBusy(button, true, "جارٍ قراءة النسخة...");
        try
        {
            var settings = ReadSettings();
            if (!HasRequiredSettings(settings)) throw new InvalidOperationException("أدخل إعدادات الاتصال أولًا.");
            var service = new BranchSetupService(settings);
            long baseline = await service.PreviewAsync();
            string message = $"القاعدة المحلية: {settings.LocalDatabase}\nالسيرفر المحلي: {settings.LocalServer}:{settings.LocalPort}\n" +
                $"آخر حركة في النسخة: {baseline}\nسيبدأ الاستقبال بعد هذا الرقم.\n\n" +
                "سيتم حفظ نسخة من سجلات المزامنة ثم تفريغها محليًا. بيانات رصيد والعدّادات تبقى كما هي.\n" +
                "هذا الخيار لنسخة سيرفر جديدة قبل بدء العمل فقط. أغلق رصيد وأي برنامج مزامنة آخر أثناء التهيئة.\n" +
                "تأكد أن الاتصال المحلي يشير إلى نسخة الفرع وليس قاعدة السيرفر الأصلية.\n\nمتابعة التهيئة؟";
            if (MessageBox.Show(this, message, "تهيئة فرع جديد", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            SetBusy(button, true, "جارٍ التهيئة...");
            await service.InitializeAsync(baseline);
            await _settingsService.SaveAsync(settings);
            SetStatus($"تمت تهيئة {settings.LocalDatabase}. يبدأ الاستقبال بعد الحركة {baseline}. اضبط رمز الفرع وتفعيل المزامنة في رصيد.", true);
        }
        catch (Exception ex) { SetStatus("تعذرت التهيئة: " + ex.Message, false); }
        finally
        {
            _syncOperationRunning = false;
            SetBusy(button, false, "تهيئة فرع جديد");
            if (timerWasRunning) StartAutomaticSync(_activeSyncIntervalMinutes);
        }
    }
}
