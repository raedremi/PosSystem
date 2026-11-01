using RasidSync.Services;

namespace RasidSync;

public partial class Form1
{
    private void AddReceiveCounterButton()
    {
        var button=CreateSyncActionButton("عداد الاستقبال",Color.FromArgb(162,121,80));
        button.Click+=async (_,_)=>await OpenReceiveCounterAsync(button);
        _actionsPanel.Controls.Add(button);
    }
    private async Task OpenReceiveCounterAsync(Button button)
    {
        if(_syncOperationRunning){SetStatus("انتظر انتهاء المزامنة الحالية.",false);return;}
        _syncOperationRunning=true;
        bool wasRunning=_syncTimer.Enabled;_syncTimer.Stop();SetBusy(button,true,"جارٍ قراءة العداد...");
        try
        {
            var settings=ReadSettings();
            if(!HasRequiredSettings(settings))throw new InvalidOperationException("أدخل إعدادات الاتصال أولًا.");
            await new DeviceAuthorizationClient(settings).EnsureAllowedAsync();
            await new SyncInfrastructureService(settings).EnsureCreatedAsync();
            var service=new ReceiveCounterService(settings);
            using var form=new ReceiveCounterForm(await service.InspectAsync());
            if(form.ShowDialog(this)!=DialogResult.OK)return;
            await service.SetManualStartAsync(form.FirstEvent);
            SetStatus("تم حفظ بداية الاستقبال من الحركة "+form.FirstEvent+". ترقيم المستندات لم يتغير.",true);
        }
        catch(Exception ex){SetStatus(ex.Message,false);}
        finally{_syncOperationRunning=false;SetBusy(button,false,"عداد الاستقبال");if(wasRunning)StartAutomaticSync(_activeSyncIntervalMinutes);}
    }
}
