using RasidSync.Models;
using RasidSync.Services;

namespace RasidSync;

public partial class Form1
{
    private Button _sendPendingEventsButton = null!;
    private Button _receiveEventsButton = null!;
    private Button _queueInvoiceButton = null!;
    private Button _syncLogsButton = null!;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_sendPendingEventsButton is not null)
            return;

        _sendPendingEventsButton = CreateSyncActionButton(
            "إرسال الحركات المعلقة",
            Color.FromArgb(181, 114, 46),
            ClientSize.Width - 430);

        _receiveEventsButton = CreateSyncActionButton(
            "استقبال الحركات الجديدة",
            Color.FromArgb(91, 83, 150),
            ClientSize.Width - 630);

        _queueInvoiceButton = CreateSyncActionButton(
            "تجهيز JSON فاتورة",
            Color.FromArgb(45, 126, 96),
            ClientSize.Width - 830);

        _syncLogsButton = CreateSyncActionButton(
            "سجلات المزامنة",
            Color.FromArgb(43, 108, 138),
            ClientSize.Width - 1030);

        _sendPendingEventsButton.Click += async (_, _) => await SendPendingEventsAsync();
        _receiveEventsButton.Click += async (_, _) => await ReceiveEventsAsync();
        _queueInvoiceButton.Click += async (_, _) => await QueueInvoiceAsync();
        _syncLogsButton.Click += async (_, _) => await OpenSyncLogsAsync();

        Controls.Add(_sendPendingEventsButton);
        Controls.Add(_receiveEventsButton);
        Controls.Add(_queueInvoiceButton);
        Controls.Add(_syncLogsButton);

        _sendPendingEventsButton.BringToFront();
        _receiveEventsButton.BringToFront();
        _queueInvoiceButton.BringToFront();
        _syncLogsButton.BringToFront();
    }

    private async Task OpenSyncLogsAsync()
    {
        try
        {
            SyncSettings settings = ReadSettings();
            await new SyncInfrastructureService(settings).EnsureCreatedAsync();
            using var form = new SyncLogsForm(settings);
            form.ShowDialog(this);
        }
        catch (Exception ex) { SetStatus(ex.Message, false); }
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

    private async Task SendPendingEventsAsync()
    {
        SetBusy(_sendPendingEventsButton, true, "جارٍ إرسال المعلّق...");

        try
        {
            SyncSettings settings = ReadSettings();
            await _settingsService.SaveAsync(settings);

            var service = new PendingSyncService();
            string result = await service.SendPendingAsync(settings);
            SetStatus(result, true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, false);
        }
        finally
        {
            SetBusy(_sendPendingEventsButton, false, "إرسال الحركات المعلقة");
        }
    }

    private async Task ReceiveEventsAsync()
    {
        SetBusy(_receiveEventsButton, true, "جارٍ الاستقبال...");

        try
        {
            SyncSettings settings = ReadSettings();
            await _settingsService.SaveAsync(settings);

            var service = new ReceiveSyncService();
            string result = await service.PullAndApplyAsync(settings);
            SetStatus(result, true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, false);
        }
        finally
        {
            SetBusy(_receiveEventsButton, false, "استقبال الحركات الجديدة");
        }
    }

    private async Task QueueInvoiceAsync()
    {
        long? invoiceId = ShowInvoiceIdDialog();
        if (invoiceId is null)
            return;

        int? operationType = ShowSyncOperationDialog();
        if (operationType is null)
            return;

        SetBusy(_queueInvoiceButton, true, "جارٍ تجهيز JSON...");

        try
        {
            SyncSettings settings = ReadSettings();
            var service = new InvoicePackageService(settings);
            InvoiceQueueResult result = await service.BuildAndQueueAsync(
                invoiceId.Value,
                operationType.Value);

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

    /// <summary>
    /// اختيار نوع الحركة بشكل واضح قبل وضع الفاتورة في جدول الإرسال.
    /// </summary>
    private int? ShowSyncOperationDialog()
    {
        using var dialog = new Form
        {
            Text = "نوع مزامنة الفاتورة",
            Size = new Size(430, 245),
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
            Text = "اختر العملية التي ستُسجل لهذه الفاتورة",
            Dock = DockStyle.Top,
            Height = 55,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold)
        };

        var operation = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 320,
            Location = new Point(48, 70),
            Font = new Font("Segoe UI", 11F)
        };
        operation.Items.Add(new SyncOperationChoice(1, "إضافة فاتورة جديدة (1)"));
        operation.Items.Add(new SyncOperationChoice(2, "تعديل فاتورة موجودة (2)"));
        operation.Items.Add(new SyncOperationChoice(4, "استبدال كامل / إعادة إرسال (4)"));
        operation.SelectedIndex = 0;

        var acceptButton = new Button
        {
            Text = "متابعة",
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(45, 126, 96),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Width = 135,
            Height = 38,
            Location = new Point(220, 135)
        };
        acceptButton.FlatAppearance.BorderSize = 0;

        var cancelButton = new Button
        {
            Text = "إلغاء",
            DialogResult = DialogResult.Cancel,
            Width = 110,
            Height = 38,
            Location = new Point(90, 135)
        };

        dialog.Controls.Add(title);
        dialog.Controls.Add(operation);
        dialog.Controls.Add(acceptButton);
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = acceptButton;
        dialog.CancelButton = cancelButton;

        return dialog.ShowDialog(this) == DialogResult.OK
            ? ((SyncOperationChoice)operation.SelectedItem!).Value
            : null;
    }

    private sealed record SyncOperationChoice(int Value, string Text)
    {
        public override string ToString() => Text;
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
