using RasidSync.Models;
using RasidSync.Services;

namespace RasidSync;

public partial class Form1
{
    private Button _sendTestEventButton = null!;
    private Button _receiveTestEventButton = null!;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_sendTestEventButton is not null)
            return;

        _sendTestEventButton = CreateSyncActionButton(
            "إرسال حركة تجريبية",
            Color.FromArgb(181, 114, 46),
            ClientSize.Width - 430);

        _receiveTestEventButton = CreateSyncActionButton(
            "استقبال حركات تجريبية",
            Color.FromArgb(91, 83, 150),
            ClientSize.Width - 630);

        _sendTestEventButton.Click += async (_, _) => await SendTestEventAsync();
        _receiveTestEventButton.Click += async (_, _) => await ReceiveTestEventsAsync();

        Controls.Add(_sendTestEventButton);
        Controls.Add(_receiveTestEventButton);
        _sendTestEventButton.BringToFront();
        _receiveTestEventButton.BringToFront();
    }

    private Button CreateSyncActionButton(string text, Color color, int x)
    {
        var button = new Button
        {
            Text = text,
            Width = 190,
            Height = 40,
            BackColor = color,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(x, ClientSize.Height - 65),
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private async Task SendTestEventAsync()
    {
        SetBusy(_sendTestEventButton, true, "جارٍ الإرسال...");

        try
        {
            SyncSettings settings = ReadSettings();
            await _settingsService.SaveAsync(settings);

            var service = new TestSyncService();
            string result = await service.CreateAndSendAsync(settings);
            SetStatus(result, true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, false);
        }
        finally
        {
            SetBusy(_sendTestEventButton, false, "إرسال حركة تجريبية");
        }
    }

    private async Task ReceiveTestEventsAsync()
    {
        SetBusy(_receiveTestEventButton, true, "جارٍ الاستقبال...");

        try
        {
            SyncSettings settings = ReadSettings();
            await _settingsService.SaveAsync(settings);

            var service = new ReceiveTestService();
            string result = await service.PullAndSaveAsync(settings);
            SetStatus(result, true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, false);
        }
        finally
        {
            SetBusy(_receiveTestEventButton, false, "استقبال حركات تجريبية");
        }
    }
}
