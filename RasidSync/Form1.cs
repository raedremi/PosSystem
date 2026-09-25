using MySqlConnector;
using RasidSync.Models;
using RasidSync.Services;

namespace RasidSync;

public partial class Form1 : Form
{
    private static readonly Color PageColor = Color.FromArgb(244, 247, 250);
    private static readonly Color PrimaryColor = Color.FromArgb(32, 102, 122);
    private static readonly Color AccentColor = Color.FromArgb(43, 137, 137);
    private static readonly Color TextColor = Color.FromArgb(39, 52, 61);
    private static readonly Color MutedColor = Color.FromArgb(103, 119, 128);

    private readonly SettingsService _settingsService = new();
    private readonly DeviceIdentityService _deviceIdentityService = new();

    private TextBox _apiUrlText = null!;
    private TextBox _onlineDatabaseText = null!;
    private TextBox _localServerText = null!;
    private NumericUpDown _localPortNumber = null!;
    private TextBox _localDatabaseText = null!;
    private TextBox _usernameText = null!;
    private TextBox _passwordText = null!;
    private TextBox _deviceUuidText = null!;
    private NumericUpDown _syncIntervalNumber = null!;
    private CheckBox _startWithWindowsCheck = null!;
    private CheckBox _minimizeToTrayCheck = null!;
    private Label _statusLabel = null!;
    private FlowLayoutPanel _actionsPanel = null!;
    private readonly ToolTip _statusHint = new();
    private Button _saveButton = null!;
    private Button _testLocalButton = null!;
    private Button _testApiButton = null!;
    private readonly System.Windows.Forms.Timer _syncTimer = new();
    private bool _syncOperationRunning;
    private int _activeSyncIntervalMinutes = 5;

    public Form1()
    {
        InitializeComponent();
        BuildInterface();
        InitializeTrayFeatures();
        Load += async (_, _) => await LoadSettingsAsync();
        _syncTimer.Tick += async (_, _) => await RunAutomaticSyncAsync();
        FormClosed += (_, _) => _syncTimer.Dispose();
    }

    private void BuildInterface()
    {
        SuspendLayout();

        Text = "Rasid Sync - إعدادات المزامنة";
        BackColor = PageColor;
        Font = new Font("Arial", 10F);
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(850, 650);
        Size = new Size(1040, 720);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = PageColor,
            ColumnCount = 1,
            RowCount = 3,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildWorkspace(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);

        Controls.Add(root);
        ResumeLayout();
    }

    private Control BuildHeader()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = PrimaryColor,
            Padding = new Padding(34, 18, 34, 14)
        };

        var title = new Label
        {
            Text = "إعدادات مزامنة رصيد",
            ForeColor = Color.White,
            Font = new Font("Arial", 20F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(34, 18)
        };

        var subtitle = new Label
        {
            Text = "ربط قاعدة البيانات المحلية بقاعدة البيانات الموجودة على السيرفر",
            ForeColor = Color.FromArgb(218, 235, 239),
            Font = new Font("Arial", 10.5F),
            AutoSize = true,
            Location = new Point(37, 61)
        };

        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        return panel;
    }

    private Control BuildContent()
    {
        var scrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = PageColor,
            Padding = new Padding(26, 22, 26, 14)
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = PageColor
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Control localCard = BuildLocalDatabaseCard();
        Control apiCard = BuildApiCard();
        Control deviceCard = BuildDeviceCard();

        localCard.Margin = new Padding(8);
        apiCard.Margin = new Padding(8);
        deviceCard.Margin = new Padding(8);

        grid.Controls.Add(localCard, 0, 0);
        grid.Controls.Add(apiCard, 1, 0);
        grid.Controls.Add(deviceCard, 0, 1);
        grid.SetColumnSpan(deviceCard, 2);

        scrollPanel.Controls.Add(grid);
        return scrollPanel;
    }

    private Control BuildWorkspace()
    {
        // لوحة الأوامر ثابتة إلى يمين الشاشة، والإعدادات تبقى في المساحة اليسرى.
        var workspace = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = PageColor,
            RightToLeft = RightToLeft.No
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 225));
        workspace.Controls.Add(BuildContent(), 0, 0);
        workspace.Controls.Add(BuildActionsPanel(), 1, 0);
        return workspace;
    }

    private Control BuildActionsPanel()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(14, 22, 14, 14)
        };

        var title = new Label
        {
            Text = "الأوامر",
            Dock = DockStyle.Top,
            Height = 42,
            Font = new Font("Arial", 13F, FontStyle.Bold),
            ForeColor = TextColor,
            TextAlign = ContentAlignment.MiddleRight
        };

        _actionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            RightToLeft = RightToLeft.Yes,
            Padding = new Padding(0, 8, 0, 0)
        };

        _saveButton = CreateButton("حفظ الإعدادات", PrimaryColor);
        _saveButton.Width = 190;
        _saveButton.Click += async (_, _) => await SaveSettingsAsync();
        _actionsPanel.Controls.Add(_saveButton);
        _testLocalButton.Width = 190;
        _testApiButton.Width = 190;
        _actionsPanel.Controls.Add(_testLocalButton);
        _actionsPanel.Controls.Add(_testApiButton);

        host.Controls.Add(_actionsPanel);
        host.Controls.Add(title);
        return host;
    }

    private Control BuildLocalDatabaseCard()
    {
        var card = CreateCard("قاعدة البيانات المحلية", "بيانات الاتصال بقاعدة رصيد الموجودة على هذا الكمبيوتر");
        var fields = CreateFieldsTable(5);

        _localServerText = CreateTextBox();
        _localPortNumber = new NumericUpDown
        {
            Dock = DockStyle.Fill,
            Minimum = 1,
            Maximum = 65535,
            Value = 3306,
            Font = new Font("Arial", 10.5F),
            TextAlign = HorizontalAlignment.Left,
            Height = 34
        };
        _localDatabaseText = CreateTextBox();
        _usernameText = CreateTextBox();
        _passwordText = CreateTextBox();
        _passwordText.UseSystemPasswordChar = true;

        AddField(fields, 0, "السيرفر أو IP", _localServerText);
        AddField(fields, 1, "المنفذ", _localPortNumber);
        AddField(fields, 2, "اسم قاعدة البيانات", _localDatabaseText);
        AddField(fields, 3, "اسم المستخدم", _usernameText);
        AddField(fields, 4, "كلمة المرور", _passwordText);

        _testLocalButton = CreateButton("اختبار الاتصال المحلي", AccentColor);
        _testLocalButton.Click += async (_, _) => await TestLocalConnectionAsync();

        card.Controls.Add(fields);
        return card;
    }

    private Control BuildApiCard()
    {
        var card = CreateCard("السيرفر والـ API", "تحديد عنوان الخدمة واسم قاعدة البيانات الموجودة أون لاين");
        var fields = CreateFieldsTable(2);

        _apiUrlText = CreateTextBox();
        _onlineDatabaseText = CreateTextBox();

        AddField(fields, 0, "عنوان API", _apiUrlText);
        AddField(fields, 1, "قاعدة السيرفر", _onlineDatabaseText);

        _testApiButton = CreateButton("اختبار اتصال API", Color.FromArgb(74, 111, 165));
        _testApiButton.Click += async (_, _) => await TestApiConnectionAsync();

        card.Controls.Add(fields);
        return card;
    }

    private Control BuildDeviceCard()
    {
        var card = CreateCard("هوية الجهاز والتشغيل التلقائي", "تحكم بمدة المزامنة وطريقة عمل البرنامج مع Windows");
        card.Height = 305;

        var fields = CreateFieldsTable(4);

        _deviceUuidText = CreateTextBox();
        _deviceUuidText.ReadOnly = true;
        _deviceUuidText.BackColor = Color.FromArgb(240, 244, 246);
        _deviceUuidText.Font = new Font("Arial", 10.5F);
        _deviceUuidText.TextAlign = HorizontalAlignment.Center;

        _syncIntervalNumber = new NumericUpDown
        {
            Dock = DockStyle.Fill,
            Minimum = 1,
            Maximum = 1440,
            Value = 5,
            Font = new Font("Arial", 10.5F),
            TextAlign = HorizontalAlignment.Center
        };

        _startWithWindowsCheck = CreateOptionCheckBox("تشغيل البرنامج تلقائيًا مع Windows");
        _minimizeToTrayCheck = CreateOptionCheckBox("عند الإغلاق، إبقاء البرنامج بجانب الساعة");

        AddField(fields, 0, "UUID الجهاز", _deviceUuidText);
        AddField(fields, 1, "المزامنة كل (دقيقة)", _syncIntervalNumber);
        AddField(fields, 2, "بدء التشغيل", _startWithWindowsCheck);
        AddField(fields, 3, "العمل في الخلفية", _minimizeToTrayCheck);
        card.Controls.Add(fields);
        return card;
    }

    private static CheckBox CreateOptionCheckBox(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        Checked = true,
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleRight,
        CheckAlign = ContentAlignment.MiddleRight,
        ForeColor = TextColor,
        Font = new Font("Arial", 10F)
    };

    private Control BuildFooter()
    {
        // شريط الحالة مستقل عن الأزرار حتى يظهر الخطأ الطويل بوضوح.
        var footer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(26, 9, 26, 12)
        };

        _statusLabel = new Label
        {
            Text = "جاهز لإدخال الإعدادات",
            ForeColor = MutedColor,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Arial", 10F),
            Padding = new Padding(12, 5, 12, 5),
            BackColor = Color.FromArgb(244, 247, 250),
            Cursor = Cursors.Hand,
            Margin = Padding.Empty
        };
        _statusLabel.Click += (_, _) => MessageBox.Show(
            this, _statusLabel.Text, "تفاصيل الحالة", MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        footer.Controls.Add(_statusLabel);
        return footer;
    }

    private static Panel CreateCard(string titleText, string subtitleText)
    {
        var card = new Panel
        {
            Dock = DockStyle.Top,
            Height = 355,
            BackColor = Color.White,
            Padding = new Padding(18)
        };

        var title = new Label
        {
            Text = titleText,
            Dock = DockStyle.Top,
            Height = 34,
            Font = new Font("Arial", 13F, FontStyle.Bold),
            ForeColor = TextColor,
            TextAlign = ContentAlignment.MiddleRight
        };

        var subtitle = new Label
        {
            Text = subtitleText,
            Dock = DockStyle.Top,
            Height = 43,
            Font = new Font("Arial", 9.5F),
            ForeColor = MutedColor,
            TextAlign = ContentAlignment.TopRight
        };

        card.Controls.Add(subtitle);
        card.Controls.Add(title);
        return card;
    }

    private static TableLayoutPanel CreateFieldsTable(int rows)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = rows * 49,
            ColumnCount = 2,
            RowCount = rows,
            Padding = new Padding(0, 4, 0, 0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));

        for (int index = 0; index < rows; index++)
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));

        return table;
    }

    private static void AddField(TableLayoutPanel table, int row, string labelText, Control input)
    {
        var label = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = TextColor,
            Font = new Font("Arial", 9.5F, FontStyle.Bold),
            Margin = new Padding(5, 4, 5, 5)
        };

        input.Margin = new Padding(5, 6, 5, 6);
        table.Controls.Add(input, 0, row);
        table.Controls.Add(label, 1, row);
    }

    private static TextBox CreateTextBox()
    {
        return new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Arial", 10.5F),
            RightToLeft = RightToLeft.No
        };
    }

    private static Button CreateButton(string text, Color color)
    {
        return new Button
        {
            Text = text,
            Height = 40,
            Width = 175,
            BackColor = color,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Arial", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
    }

    private async Task LoadSettingsAsync()
    {
        SyncSettings settings = await _settingsService.LoadAsync();

        // UUID هو هوية للكمبيوتر نفسه، لذلك لا نأخذه من settings.json القابل للنسخ.
        string deviceUuid = await _deviceIdentityService.GetOrCreateAsync();
        if (!string.Equals(settings.DeviceUuid, deviceUuid, StringComparison.OrdinalIgnoreCase))
        {
            settings.DeviceUuid = deviceUuid;
            await _settingsService.SaveAsync(settings);
        }

        _apiUrlText.Text = settings.ApiUrl;
        _onlineDatabaseText.Text = settings.OnlineDatabase;
        _localServerText.Text = settings.LocalServer;
        _localPortNumber.Value = Math.Clamp(settings.LocalPort, 1u, 65535u);
        _localDatabaseText.Text = settings.LocalDatabase;
        _usernameText.Text = settings.LocalUsername;
        _passwordText.Text = settings.LocalPassword;
        _deviceUuidText.Text = settings.DeviceUuid;
        _syncIntervalNumber.Value = Math.Clamp(settings.SyncIntervalMinutes, 1, 1440);
        _startWithWindowsCheck.Checked = settings.StartWithWindows;
        _minimizeToTrayCheck.Checked = settings.MinimizeToTray;

        // يحدّث مسار التشغيل إذا نُقل البرنامج إلى مجلد آخر.
        WindowsStartupService.Apply(settings.StartWithWindows);

        SetStatus("تم تحميل الإعدادات. أدخل البيانات ثم اختبر الاتصال.", true);
        if (HasRequiredSettings(settings))
            StartAutomaticSync(settings.SyncIntervalMinutes);

        ApplyStartupWindowMode(settings);
    }

    private SyncSettings ReadSettings()
    {
        return new SyncSettings
        {
            ApiUrl = _apiUrlText.Text.Trim().TrimEnd('/'),
            OnlineDatabase = _onlineDatabaseText.Text.Trim(),
            LocalServer = _localServerText.Text.Trim(),
            LocalPort = (uint)_localPortNumber.Value,
            LocalDatabase = _localDatabaseText.Text.Trim(),
            LocalUsername = _usernameText.Text.Trim(),
            LocalPassword = _passwordText.Text,
            DeviceUuid = _deviceUuidText.Text.Trim(),
            SyncIntervalMinutes = (int)_syncIntervalNumber.Value,
            StartWithWindows = _startWithWindowsCheck.Checked,
            MinimizeToTray = _minimizeToTrayCheck.Checked
        };
    }

    private static bool HasRequiredSettings(SyncSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.ApiUrl) &&
        !string.IsNullOrWhiteSpace(settings.OnlineDatabase) &&
        !string.IsNullOrWhiteSpace(settings.LocalServer) &&
        !string.IsNullOrWhiteSpace(settings.LocalDatabase);

    private async Task SaveSettingsAsync()
    {
        SyncSettings settings = ReadSettings();

        if (string.IsNullOrWhiteSpace(settings.ApiUrl) ||
            string.IsNullOrWhiteSpace(settings.LocalServer) ||
            string.IsNullOrWhiteSpace(settings.LocalDatabase) ||
            string.IsNullOrWhiteSpace(settings.OnlineDatabase))
        {
            SetStatus("يرجى تعبئة عنوان API وقاعدتي البيانات وبيانات السيرفر المحلي.", false);
            return;
        }

        try
        {
            await _settingsService.SaveAsync(settings);
            WindowsStartupService.Apply(settings.StartWithWindows);
            StartAutomaticSync(settings.SyncIntervalMinutes);
            SetStatus($"تم حفظ الإعدادات. المزامنة التلقائية كل {settings.SyncIntervalMinutes} دقيقة.", true);
        }
        catch (Exception ex)
        {
            SetStatus($"تعذر حفظ الإعدادات: {ex.Message}", false);
        }
    }

    private async Task TestLocalConnectionAsync()
    {
        SetBusy(_testLocalButton, true, "جارٍ الاختبار...");
        try
        {
            SyncSettings settings = ReadSettings();
            var connectionString = new MySqlConnectionStringBuilder
            {
                Server = settings.LocalServer,
                Port = settings.LocalPort,
                Database = settings.LocalDatabase,
                UserID = settings.LocalUsername,
                Password = settings.LocalPassword,
                ConnectionTimeout = 10,
                DefaultCommandTimeout = 15
            };

            await using var connection = new MySqlConnection(connectionString.ConnectionString);
            await connection.OpenAsync();

            SetStatus($"نجح الاتصال بقاعدة البيانات المحلية: {settings.LocalDatabase}", true);
        }
        catch (Exception ex)
        {
            SetStatus($"فشل الاتصال المحلي: {ex.Message}", false);
        }
        finally
        {
            SetBusy(_testLocalButton, false, "اختبار الاتصال المحلي");
        }
    }

    private async Task TestApiConnectionAsync()
    {
        SetBusy(_testApiButton, true, "جارٍ الاختبار...");
        try
        {
            SyncSettings settings = ReadSettings();

            if (!Uri.TryCreate(settings.ApiUrl, UriKind.Absolute, out _))
                throw new InvalidOperationException("عنوان API غير صحيح.");

            if (string.IsNullOrWhiteSpace(settings.OnlineDatabase))
                throw new InvalidOperationException("اسم قاعدة بيانات السيرفر مطلوب.");

            SyncDeviceCheckResponse result =
                await new DeviceAuthorizationClient(settings).CheckAsync();

            SetStatus(result.Allowed
                ? $"الاتصال ناجح والجهاز مسموح له بالمزامنة: {settings.OnlineDatabase}"
                : result.Message,
                result.Allowed);
        }
        catch (Exception ex)
        {
            SetStatus($"فشل اتصال API: {ex.Message}", false);
        }
        finally
        {
            SetBusy(_testApiButton, false, "اختبار اتصال API");
        }
    }

    private void SetStatus(string message, bool success)
    {
        _statusLabel.Text = message;
        _statusHint.SetToolTip(_statusLabel, message);
        _statusLabel.ForeColor = success
            ? Color.FromArgb(31, 132, 92)
            : Color.FromArgb(190, 63, 63);
        SetTrayStatus(success ? SyncTrayState.Success : SyncTrayState.Error, message);
    }

    private static void SetBusy(Button button, bool busy, string text)
    {
        button.Enabled = !busy;
        button.Text = text;
    }
}
