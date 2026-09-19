using RasidSync.Models;
using RasidSync.Services;

namespace RasidSync;

public partial class Form1
{
    private Button _sendTestEventButton = null!;
    private Button _receiveTestEventButton = null!;
    private Button _queueInvoiceButton = null!;

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

        _queueInvoiceButton = CreateSyncActionButton(
            "تجهيز JSON فاتورة",
            Color.FromArgb(45, 126, 96),
            ClientSize.Width - 830);

        _sendTestEventButton.Click += async (_, _) => await SendTestEventAsync();
        _receiveTestEventButton.Click += async (_, _) => await ReceiveTestEventsAsync();
        _queueInvoiceButton.Click += async (_, _) => await QueueInvoiceAsync();

        Controls.Add(_sendTestEventButton);
        Controls.Add(_receiveTestEventButton);
        Controls.Add(_queueInvoiceButton);

        _sendTestEventButton.BringToFront();
        _receiveTestEventButton.BringToFront();
        _queueInvoiceButton.BringToFront();
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

    private async Task QueueInvoiceAsync()
    {
        long? invoiceId = ShowInvoiceIdDialog();
        if (invoiceId is null)
            return;

        SetBusy(_queueInvoiceButton, true, "جارٍ تجهيز JSON...");

        try
        {
            SyncSettings settings = ReadSettings();
            var service = new InvoicePackageService(settings);
            InvoiceQueueResult result = await service.BuildAndQueueAsync(invoiceId.Value);

            string dueText = result.HasDue ? "مع استحقاق" : "بدون استحقاق";
            SetStatus(
                $"تم تجهيز الفاتورة {result.InvoiceId} في الحركة {result.SyncId}: " +
                $"{result.DetailCount} بند، {result.PaymentCount} دفعة، {dueText}.",
                true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, false);
        }
        finally
        {
            SetBusy(_queueInvoiceButton, false, "تجهيز JSON فاتورة");
        }
    }

    private long? ShowInvoiceIdDialog()
    {
        using var dialog = new Form
        {
            Text = "اختيار فاتورة",
            Size = new Size(390, 210),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            RightToLeft = RightToLeft.Yes,
            RightToLeftLayout = true,
            BackColor = Color.White,
            Font = new Font("Segoe UI", 10F)
        };

        var title = new Label
        {
            Text = "أدخل رقم ID الداخلي للفاتورة (inv_id)",
            Dock = DockStyle.Top,
            Height = 55,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold)
        };

        var invoiceNumber = new NumericUpDown
        {
            Minimum = 1,
            Maximum = long.MaxValue,
            Width = 280,
            Height = 36,
            Font = new Font("Segoe UI", 12F),
            TextAlign = HorizontalAlignment.Center,
            Location = new Point(48, 65)
        };

        var acceptButton = new Button
        {
            Text = "تجهيز الفاتورة",
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(45, 126, 96),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Width = 140,
            Height = 38,
            Location = new Point(188, 115)
        };
        acceptButton.FlatAppearance.BorderSize = 0;

        var cancelButton = new Button
        {
            Text = "إلغاء",
            DialogResult = DialogResult.Cancel,
            Width = 110,
            Height = 38,
            Location = new Point(68, 115)
        };

        dialog.Controls.Add(title);
        dialog.Controls.Add(invoiceNumber);
        dialog.Controls.Add(acceptButton);
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = acceptButton;
        dialog.CancelButton = cancelButton;

        return dialog.ShowDialog(this) == DialogResult.OK
            ? decimal.ToInt64(invoiceNumber.Value)
            : null;
    }
}
