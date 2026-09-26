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
    private readonly TextBox _searchBox = new() { PlaceholderText = "رقم الحركة، UUID أو الخطأ", Width = 190 };
    private readonly DateTimePicker _fromDate = CreateDatePicker();
    private readonly DateTimePicker _toDate = CreateDatePicker();
    private readonly Label _resultsLabel = new() { AutoSize = true };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly List<Control> _errorOnlyControls = [];
    private List<SyncLogRow> _sentRows = [];
    private List<SyncLogRow> _receivedRows = [];
    private List<SyncErrorRow> _errorRows = [];

    public SyncLogsForm(SyncSettings settings)
    {
        _repository = new SyncLogRepository(settings);
        Text = "سجلات المزامنة";
        Size = new Size(1250, 780);
        MinimumSize = new Size(850, 560);
        StartPosition = FormStartPosition.CenterParent;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        Font = new Font("Arial", 10F);
        BackColor = Color.FromArgb(245, 247, 250);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            BackColor = BackColor, RightToLeft = RightToLeft.No
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 245));

        _fromDate.Value = DateTime.Today;
        _toDate.Value = DateTime.Today;
        _searchBox.TextChanged += (_, _) => ApplyFilter();

        _tabs.TabPages.Add(CreateLogPage("الحركات المرسلة", _sendGrid));
        _tabs.TabPages.Add(CreateLogPage("الحركات المستقبلة", _receiveGrid));
        _tabs.TabPages.Add(CreateErrorsPage());
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            ApplyFilter();
            UpdateErrorActionsVisibility();
        };
        root.Controls.Add(_tabs, 0, 0);
        root.Controls.Add(BuildRightPanel(), 1, 0);
        Controls.Add(root);

        ConfigureColumns();
        _sendGrid.SelectionChanged += (_, _) => UpdateDetails();
        _receiveGrid.SelectionChanged += (_, _) => UpdateDetails();
        _errorGrid.SelectionChanged += (_, _) => UpdateDetails();

        UpdateErrorActionsVisibility();
        Shown += async (_, _) => await ReloadAsync();
    }

    private Control BuildRightPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, BackColor = Color.White,
            Padding = new Padding(16), RightToLeft = RightToLeft.Yes
        };

        panel.Controls.Add(new Label
        {
            Text = "بحث السجلات", Width = 195, Height = 40,
            Font = new Font("Arial", 14F, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 60, 75), TextAlign = ContentAlignment.MiddleRight
        });
        panel.Controls.Add(CreateSideLabel("من تاريخ"));
        panel.Controls.Add(_fromDate);
        panel.Controls.Add(CreateSideLabel("إلى تاريخ"));
        panel.Controls.Add(_toDate);
        panel.Controls.Add(CreateSideLabel("بحث ضمن النتائج"));
        panel.Controls.Add(_searchBox);
        panel.Controls.Add(CreateButton("بحث", ReloadAsync));
        panel.Controls.Add(CreateButton("حركات اليوم", ShowTodayAsync, Color.FromArgb(45, 126, 96)));
        panel.Controls.Add(_resultsLabel);
        AddErrorOnlyControl(panel, CreateSeparator());
        AddErrorOnlyControl(panel, CreateSideLabel("معالجة الأخطاء"));
        AddErrorOnlyControl(panel, CreateButton("إعادة المحاولة", RetrySelectedAsync));
        AddErrorOnlyControl(panel, CreateButton("فتح التفاصيل", ShowDetailsAsync));
        AddErrorOnlyControl(panel, CreateButton("فتح الفاتورة المحلية", ShowLocalInvoiceAsync));
        AddErrorOnlyControl(panel, CreateButton("إلغاء الحركة", CancelSelectedAsync, Color.Firebrick));
        AddErrorOnlyControl(panel, CreateButton("اعتبارهما نفس الفاتورة - دعم", SupportMergeAsync, Color.DarkSlateBlue));
        AddErrorOnlyControl(panel, CreateButton("تصدير الخطأ", ExportSelectedAsync));
        return panel;
    }

    private void AddErrorOnlyControl(Control parent, Control control)
    {
        _errorOnlyControls.Add(control);
        parent.Controls.Add(control);
    }

    /// <summary>أزرار القرار الإداري تظهر فقط في تبويب الأخطاء.</summary>
    private void UpdateErrorActionsVisibility()
    {
        bool show = _tabs.SelectedIndex == 2;
        foreach (Control control in _errorOnlyControls)
            control.Visible = show;
    }

    private static Label CreateSideLabel(string text) => new()
    {
        Text = text, Width = 195, Height = 25,
        Font = new Font("Arial", 10F, FontStyle.Bold),
        ForeColor = Color.FromArgb(55, 75, 88), TextAlign = ContentAlignment.BottomRight,
        Margin = new Padding(0, 7, 0, 0)
    };

    private static DateTimePicker CreateDatePicker() => new()
    {
        Width = 195, Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd", Font = new Font("Arial", 10F)
    };

    private static Panel CreateSeparator() => new()
    {
        Width = 195, Height = 1, BackColor = Color.FromArgb(220, 228, 233),
        Margin = new Padding(0, 15, 0, 8)
    };

    private TabPage CreateErrorsPage()
    {
        return CreateLogPage("الأخطاء", _errorGrid);
    }

    private TabPage CreateLogPage(string title, DataGridView grid)
    {
        var page = new TabPage(title) { Padding = new Padding(8), BackColor = BackColor };
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            SplitterWidth = 8, Panel1MinSize = 140, Panel2MinSize = 140
        };
        split.Panel1.Controls.Add(grid);
        var details = new TabControl { Dock = DockStyle.Fill };
        var explanation = new TabPage("بيانات الحركة وسبب الخطأ");
        var payload = new TabPage("JSON الكامل");
        var reasonText = CreateDetailsBox();
        reasonText.RightToLeft = RightToLeft.Yes;
        reasonText.WordWrap = true;
        reasonText.ScrollBars = ScrollBars.Vertical;
        reasonText.Font = new Font("Arial", 10.5F);
        var payloadText = CreateDetailsBox();
        explanation.Controls.Add(reasonText);
        payload.Controls.Add(payloadText);
        details.TabPages.Add(explanation);
        details.TabPages.Add(payload);
        split.Panel2.Controls.Add(details);
        page.Controls.Add(split);
        page.Tag = (reasonText, payloadText);
        // تأخير تعيين حجم الفاصل حتى يُعرف الحجم الفعلي للشاشة.
        bool firstDisplay = true;
        page.Enter += (_, _) =>
        {
            if (!firstDisplay || split.Height <= 400) return;
            firstDisplay = false;
            split.SplitterDistance = (split.Height - split.SplitterWidth) * 60 / 100;
        };
        return page;
    }

    private static TextBox CreateDetailsBox() => new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false,
        Font = new Font("Arial", 10F), BackColor = Color.White,
        BorderStyle = BorderStyle.None, RightToLeft = RightToLeft.No
    };

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        AllowUserToResizeColumns = true,
        AllowUserToOrderColumns = true,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None,
        RightToLeft = RightToLeft.Yes,
        AutoGenerateColumns = false,
        RowHeadersVisible = false,
        ColumnHeadersHeight = 39,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        EnableHeadersVisualStyles = false,
        GridColor = Color.FromArgb(230, 235, 242)
    };

    private static void AddColumn(DataGridView grid, string property, string title, int width, bool fill = false)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = property, Name = property, HeaderText = title,
            Width = width, MinimumWidth = 85,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None
        });
    }

    private void ConfigureColumns()
    {
        foreach (DataGridView grid in new[] { _sendGrid, _receiveGrid })
        {
            AddColumn(grid, nameof(SyncLogRow.Id), "رقم الحركة", 105);
            AddColumn(grid, nameof(SyncLogRow.EntityType), "النوع", 100);
            AddColumn(grid, nameof(SyncLogRow.LocalId), "ID المحلي", 105);
            AddColumn(grid, nameof(SyncLogRow.OperationType), "العملية", 95);
            AddColumn(grid, nameof(SyncLogRow.Status), "الحالة", 105);
            AddColumn(grid, nameof(SyncLogRow.RetryCount), "المحاولات", 100);
            AddColumn(grid, nameof(SyncLogRow.CreatedAt), "الوقت", 170);
            AddColumn(grid, nameof(SyncLogRow.EntityUuid), "UUID الفاتورة", 280);
            AddColumn(grid, nameof(SyncLogRow.Error), "سبب الخطأ", 340, true);
        }
        AddColumn(_errorGrid, nameof(SyncErrorRow.ErrorId), "رقم الخطأ", 105);
        AddColumn(_errorGrid, nameof(SyncErrorRow.Direction), "الاتجاه", 95);
        AddColumn(_errorGrid, nameof(SyncErrorRow.RelatedId), "رقم الحركة", 105);
        AddColumn(_errorGrid, nameof(SyncErrorRow.LocalId), "ID المحلي", 105);
        AddColumn(_errorGrid, nameof(SyncErrorRow.OperationType), "العملية", 95);
        AddColumn(_errorGrid, nameof(SyncErrorRow.ErrorCode), "تصنيف الخطأ", 190);
        AddColumn(_errorGrid, nameof(SyncErrorRow.ResolutionStatus), "الحالة", 105);
        AddColumn(_errorGrid, nameof(SyncErrorRow.CreatedAt), "الوقت", 170);
        AddColumn(_errorGrid, nameof(SyncErrorRow.EntityUuid), "UUID الفاتورة", 280);
        AddColumn(_errorGrid, nameof(SyncErrorRow.ErrorMessage), "سبب الخطأ", 360, true);

        foreach (DataGridView grid in new[] { _sendGrid, _receiveGrid, _errorGrid })
        {
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(225, 239, 244);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(35, 55, 70);
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Arial", 10F, FontStyle.Bold);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(207, 234, 242);
            grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(25, 45, 60);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 250, 252);
            grid.CellFormatting += (_, e) => FormatCell(grid, e);
        }
    }

    private static void FormatCell(DataGridView grid, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Value is not int number) return;
        string column = grid.Columns[e.ColumnIndex].Name;
        if (column == nameof(SyncLogRow.OperationType))
            e.Value = OperationName(number);
        else if (column == nameof(SyncLogRow.Status))
            e.Value = number switch
            {
                0 => "معلّقة", 1 => "جارٍ التنفيذ", 2 => "نجحت", 3 => "فشلت",
                4 => "أُلغيت", 5 => "تحتاج معالجة", _ => number.ToString()
            };
        else if (column == nameof(SyncErrorRow.ResolutionStatus))
            e.Value = number switch
            {
                0 => "تحتاج معالجة", 1 => "تمت المعالجة", 2 => "أُلغيت", 3 => "بانتظار الإعادة",
                _ => number.ToString()
            };
        else return;
        e.FormattingApplied = true;
    }

    private static string OperationName(int number) => number switch
    {
        1 => "إضافة (1)", 2 => "تعديل (2)", 3 => "حذف (3)", 4 => "استبدال (4)",
        _ => number.ToString()
    };

    private static Button CreateButton(string text, Func<Task> action, Color? color = null)
    {
        var button = new Button
        {
            Text = text, Width = 195, Height = 38,
            BackColor = color ?? Color.FromArgb(43, 108, 138),
            ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Font = new Font("Arial", 9.5F, FontStyle.Bold),
            Margin = new Padding(0, 4, 0, 4)
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += async (_, _) => await action();
        return button;
    }

    private async Task ReloadAsync()
    {
        try
        {
            DateTime from = _fromDate.Value.Date;
            DateTime to = _toDate.Value.Date;
            if (from > to)
                throw new InvalidOperationException("تاريخ البداية يجب أن يكون قبل تاريخ النهاية.");

            DateTime toExclusive = to.AddDays(1);
            _sentRows = await _repository.GetSendRowsAsync(from, toExclusive);
            _receivedRows = await _repository.GetReceiveRowsAsync(from, toExclusive);
            _errorRows = await _repository.GetErrorsAsync(from, toExclusive);
            ApplyFilter();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "تعذر تحميل السجلات", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task ShowTodayAsync()
    {
        _fromDate.Value = DateTime.Today;
        _toDate.Value = DateTime.Today;
        await ReloadAsync();
    }

    private void ApplyFilter()
    {
        string search = _searchBox.Text.Trim();
        bool Match(string? value) => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

        _sendGrid.DataSource = _sentRows.Where(x => search.Length == 0 || Match(x.Id.ToString()) ||
            Match(x.LocalId?.ToString()) || Match(x.EntityUuid) || Match(x.EntityType) || Match(x.Error)).ToList();
        _receiveGrid.DataSource = _receivedRows.Where(x => search.Length == 0 || Match(x.Id.ToString()) ||
            Match(x.EntityUuid) || Match(x.EntityType) || Match(x.Error)).ToList();
        _errorGrid.DataSource = _errorRows.Where(x => search.Length == 0 || Match(x.ErrorId.ToString()) ||
            Match(x.RelatedId.ToString()) || Match(x.LocalId?.ToString()) || Match(x.EntityUuid) ||
            Match(x.ErrorCode) || Match(x.ErrorMessage)).ToList();

        _resultsLabel.Text = $"النتائج: {_tabs.SelectedIndex switch { 0 => _sendGrid.RowCount, 1 => _receiveGrid.RowCount, _ => _errorGrid.RowCount }}";
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        if (_tabs.SelectedTab?.Tag is not ValueTuple<TextBox, TextBox> boxes) return;
        object? selected = _tabs.SelectedIndex switch
        {
            0 => _sendGrid.CurrentRow?.DataBoundItem,
            1 => _receiveGrid.CurrentRow?.DataBoundItem,
            _ => _errorGrid.CurrentRow?.DataBoundItem
        };

        if (selected is SyncErrorRow error)
        {
            boxes.Item1.Text = $"رقم الخطأ: {error.ErrorId}\r\nالاتجاه: {error.Direction}\r\n" +
                $"رقم الحركة: {error.RelatedId}\r\nID الفاتورة المحلي: {error.LocalId?.ToString() ?? "غير متوفر"}\r\n" +
                $"UUID الفاتورة المرسلة: {error.EntityUuid}\r\nالعملية: {OperationName(error.OperationType)}\r\n" +
                $"تصنيف الخطأ: {error.ErrorCode}\r\nوقت الخطأ: {error.CreatedAt:yyyy-MM-dd HH:mm:ss}\r\n\r\n" +
                $"سبب الخطأ كاملًا:\r\n{error.ErrorMessage}";
            boxes.Item2.Text = FormatJson(error.ErrorDetails);
        }
        else if (selected is SyncLogRow row)
        {
            boxes.Item1.Text = $"رقم الحركة: {row.Id}\r\nID الفاتورة المحلي: {row.LocalId?.ToString() ?? "غير متوفر"}\r\n" +
                $"UUID الفاتورة: {row.EntityUuid}\r\nالنوع: {row.EntityType}\r\n" +
                $"العملية: {OperationName(row.OperationType)}\r\nالمحاولات: {row.RetryCount}\r\n" +
                $"وقت الحركة: {row.CreatedAt:yyyy-MM-dd HH:mm:ss}\r\n\r\n" +
                $"سبب الخطأ كاملًا:\r\n{row.Error ?? "لا يوجد خطأ."}";
            boxes.Item2.Text = "تفاصيل JSON متاحة في صفحة الأخطاء للحركات المسجلة فيها.";
        }
        else boxes.Item1.Text = boxes.Item2.Text = "اختر حركة من الجدول لعرض التفاصيل.";
    }

    private static string FormatJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "لا توجد تفاصيل إضافية.";
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return value; }
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
            SelectedError();
            UpdateDetails();
            if (_tabs.SelectedTab?.Controls.OfType<SplitContainer>().FirstOrDefault() is { } split)
                split.Panel2.Controls.OfType<TabControl>().First().SelectedIndex = 0;
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
