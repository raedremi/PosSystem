using RasidSync.Models;

namespace RasidSync;

internal enum SyncTrayState
{
    Ready,
    Working,
    Success,
    Error
}

public partial class Form1
{
    private NotifyIcon _trayIcon = null!;
    private Icon? _trayStatusIcon;
    private bool _allowApplicationExit;
    private bool _trayHintShown;

    private void InitializeTrayFeatures()
    {
        var menu = new ContextMenuStrip
        {
            RightToLeft = RightToLeft.Yes,
            Font = new Font("Arial", 10F)
        };

        menu.Items.Add("فتح برنامج المزامنة", null, (_, _) => ShowMainWindow());
        menu.Items.Add("مزامنة الآن", null, async (_, _) => await RunAutomaticSyncAsync());
        menu.Items.Add("سجلات المزامنة", null, async (_, _) =>
        {
            ShowMainWindow();
            await OpenSyncLogsAsync();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("إيقاف البرنامج", null, (_, _) => ExitApplication());

        _trayIcon = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = menu,
            Text = "Rasid Sync - جاهز"
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        SetTrayStatus(SyncTrayState.Ready, "Rasid Sync - جاهز");

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && _minimizeToTrayCheck.Checked)
                HideToTray();
        };
        FormClosing += HandleFormClosing;
        FormClosed += (_, _) => DisposeTrayIcon();
    }

    private void ApplyStartupWindowMode(SyncSettings settings)
    {
        bool startedByWindows = Environment.GetCommandLineArgs()
            .Any(x => string.Equals(x, "--minimized", StringComparison.OrdinalIgnoreCase));

        if (startedByWindows && settings.MinimizeToTray)
            BeginInvoke(new Action(HideToTray));
    }

    private void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowApplicationExit ||
            e.CloseReason != CloseReason.UserClosing ||
            !_minimizeToTrayCheck.Checked)
            return;

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;

        if (_trayHintShown)
            return;

        _trayHintShown = true;
        _trayIcon.ShowBalloonTip(
            2500,
            "Rasid Sync يعمل في الخلفية",
            "ستستمر المزامنة تلقائيًا. اضغط مرتين على الأيقونة لفتح البرنامج.",
            ToolTipIcon.Info);
    }

    private void ShowMainWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _allowApplicationExit = true;
        Close();
    }

    private void SetTrayStatus(SyncTrayState state, string message)
    {
        if (_trayIcon is null)
            return;

        Color color = state switch
        {
            SyncTrayState.Working => Color.FromArgb(221, 161, 45),
            SyncTrayState.Success => Color.FromArgb(39, 154, 104),
            SyncTrayState.Error => Color.FromArgb(203, 70, 70),
            _ => Color.FromArgb(43, 108, 138)
        };

        _trayStatusIcon?.Dispose();
        _trayStatusIcon = TrayIconFactory.Create(color);
        _trayIcon.Icon = _trayStatusIcon;

        string shortMessage = message.Length > 60 ? message[..60] : message;
        _trayIcon.Text = shortMessage;
    }

    private void DisposeTrayIcon()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayStatusIcon?.Dispose();
    }
}
