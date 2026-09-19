using RasidSync.Models;
using RasidSync.Services;

namespace RasidSync;

public partial class Form1
{
    private Button _sendTestEventButton = null!;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_sendTestEventButton is not null)
            return;

        _sendTestEventButton = new Button
        {
            Text = "إرسال حركة تجريبية",
            Width = 190,
            Height = 40,
            BackColor = Color.FromArgb(181, 114, 46),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(ClientSize.Width - 430, ClientSize.Height - 65),
            UseVisualStyleBackColor = false
        };
        _sendTestEventButton.FlatAppearance.BorderSize = 0;
        _sendTestEventButton.Click += async (_, _) => await SendTestEventAsync();

        Controls.Add(_sendTestEventButton);
        _sendTestEventButton.BringToFront();
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
}
