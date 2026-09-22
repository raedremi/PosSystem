using System.Text.Json;
using RasidSync.Models;

namespace RasidSync.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public SettingsService()
    {
        // نحفظ الملف بجانب RasidSync.exe ليسهل نقل البرنامج مع إعداداته.
        _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

        // نقل إعدادات النسخ السابقة مرة واحدة حتى لا يضطر المستخدم لإدخالها مجددًا.
        string oldSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RasidSync",
            "settings.json");

        if (!File.Exists(_settingsPath) && File.Exists(oldSettingsPath))
            File.Copy(oldSettingsPath, _settingsPath);
    }

    public async Task<SyncSettings> LoadAsync()
    {
        if (!File.Exists(_settingsPath))
        {
            var newSettings = new SyncSettings();
            await SaveAsync(newSettings);
            return newSettings;
        }

        try
        {
            string json = await File.ReadAllTextAsync(_settingsPath);
            SyncSettings? settings = JsonSerializer.Deserialize<SyncSettings>(json, JsonOptions);

            if (settings is null)
                return new SyncSettings();

            if (!Guid.TryParse(settings.DeviceUuid, out _))
                settings.DeviceUuid = Guid.NewGuid().ToString();

            return settings;
        }
        catch
        {
            return new SyncSettings();
        }
    }

    public async Task SaveAsync(SyncSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json);
    }
}
