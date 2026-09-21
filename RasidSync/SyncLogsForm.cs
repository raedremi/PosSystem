using System.Text.Json;
using RasidSync.Models;
using RasidSync.Services;

namespace RasidSync;

/// <summary>نافذة واحدة مرتبة تحتوي صفحات الإرسال والاستقبال والأخطاء.</summary>
public sealed class SyncLogsForm : Form
{
    private readonly SyncLogRepository _repository;
    private readonly DataGridView _sendGrid = CreateGrid();
    private readonly DataGridView _receiveGrid = CreateGrid();
    private readonly DataGridView _errorGrid = CreateGrid();

    public SyncLogsForm(SyncSettings settings)
    {
        _repository = new SyncLogRepository(settings);
        Text = "سجلات المزامنة";
        Width = 1180;
        Height = 720;
        StartPosition = FormStartPosition.CenterParent;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.FromArgb(245, 247, 250);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateLogPage("الحركات المرسلة", _sendGrid));
        tabs.TabPages.Add(CreateLogPage("الحركات المستقبلة", _receiveGrid));
        tabs.TabPages.Add(CreateErrorsPage());
        Controls.Add(tabs);

        Shown += async (_, _) => await ReloadAsync();
    }

    private TabPage CreateErrorsPage()
    {
        var page = CreateLogPage("الأخطاء", _errorGrid);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 58,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8)
        };
        actions.Controls.Add(CreateButton("تحديث", async () => await ReloadAsync()));
        actions.Controls.Add(CreateButton("إعادة المحاولة", RetrySelectedAsync));
        actions.Controls.Add(CreateButton("فتح التفاصيل", ShowDetailsAsync));
        actions.Controls.Add(CreateButton("فتح الفاتورة المحلية", ShowLocalInvoiceAsync));
        actions.Controls.Add(CreateButton("إلغاء الحركة", CancelSelectedAsync, Color.Firebrick));
        actions.Controls.Add(CreateButton("اعتبارهما نفس الفاتورة - دعم", SupportMergeAsync, Color.DarkSlateBlue));
        actions.Controls.Add(CreateButton("تصدير الخطأ", ExportSelectedAsync));
        page.Controls.Add(actions);
        actions.BringToFront();
        return page;
    }

    private static TabPage CreateLogPage(string title, DataGridView grid)
    {
        var page = new TabPage(title) { Padding = new Padding(8) };
        page.Controls.Add(grid);
        return page;
    }

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None
    };

    private static Button CreateButton(string text, Func<Task> action, Color? color = null)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, Height = 36,
            BackColor = color ?? Color.FromArgb(43, 108, 138),
            ForeColor = Color.White, FlatStyle = FlatStyle.Flat
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += async (_, _) => await action();
        return button;
    }

    private async Task ReloadAsync()
    {
        try
        {
            _sendGrid.DataSource = await _repository.GetSendRowsAsync();
            _receiveGrid.DataSource = await _repository.GetReceiveRowsAsync();
            _errorGrid.DataSource = await _repository.GetErrorsAsync();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "خطأ", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private SyncErrorRow SelectedError() =>
        _errorGrid.CurrentRow?.DataBoundItem as SyncErrorRow
        ?? throw new InvalidOperationException("اختر خطأ من الجدول أولًا.");

    private async Task RetrySelectedAsync()
    {
        try { await _repository.RetryAsync(SelectedError()); await ReloadAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    private Task ShowDetailsAsync()
    {
        try
        {
            SyncErrorRow error = SelectedError();
            string details = error.ErrorDetails ?? "لا توجد تفاصيل إضافية.";
            try { details = JsonSerializer.Serialize(JsonDocument.Parse(details).RootElement, new JsonSerializerOptions { WriteIndented = true }); }
            catch { }
            MessageBox.Show($"رمز الخطأ: {error.ErrorCode}\n\n{error.ErrorMessage}\n\n{details}", "تفاصيل الحركة");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
        return Task.CompletedTask;
    }

    private Task ShowLocalInvoiceAsync()
    {
        try
        {
            SyncErrorRow error = SelectedError();
            MessageBox.Show(error.LocalId.HasValue
                ? $"رقم الفاتورة الداخلي في قاعدة البيانات المحلية: {error.LocalId}\nUUID: {error.EntityUuid}"
                : $"لا يوجد رقم محلي. UUID: {error.EntityUuid}", "الفاتورة المحلية");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
        return Task.CompletedTask;
    }

    private async Task CancelSelectedAsync()
    {
        try
        {
            string? password = PromptPassword("إلغاء الحركة يحتاج كلمة مرور الدعم الفني");
            if (password is null) return;
            await _repository.CancelAsync(SelectedError(), password);
            await ReloadAsync();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    private Task SupportMergeAsync()
    {
        MessageBox.Show(
            "هذه العملية محجوزة للدعم الفني. يجب فحص الفاتورتين وتحديد UUID الصحيح قبل أي دمج.",
            "عملية دعم فني", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return Task.CompletedTask;
    }

    private Task ExportSelectedAsync()
    {
        try
        {
            SyncErrorRow error = SelectedError();
            using var dialog = new SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json", FileName = $"sync-error-{error.ErrorId}.json"
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(error, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
        return Task.CompletedTask;
    }

    private static string? PromptPassword(string title)
    {
        using var dialog = new Form { Text = title, Width = 430, Height = 170, StartPosition = FormStartPosition.CenterParent, RightToLeft = RightToLeft.Yes };
        var box = new TextBox { PasswordChar = '●', Width = 330, Location = new Point(35, 28) };
        var ok = new Button { Text = "تأكيد", DialogResult = DialogResult.OK, Location = new Point(220, 75), Width = 110 };
        var cancel = new Button { Text = "إلغاء", DialogResult = DialogResult.Cancel, Location = new Point(90, 75), Width = 110 };
        dialog.Controls.AddRange([box, ok, cancel]); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        return dialog.ShowDialog() == DialogResult.OK ? box.Text : null;
    }
}
