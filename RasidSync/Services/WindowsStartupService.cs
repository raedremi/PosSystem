using Microsoft.Win32;

namespace RasidSync.Services;

/// <summary>
/// يضيف البرنامج إلى بدء تشغيل Windows للمستخدم الحالي فقط.
/// </summary>
public static class WindowsStartupService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApplicationName = "RasidSync";

    public static void Apply(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);

        if (enabled)
        {
            string command = $"\"{Application.ExecutablePath}\" --minimized";
            key.SetValue(ApplicationName, command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ApplicationName, false);
        }
    }
}
