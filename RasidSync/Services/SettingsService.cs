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
        string settingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RasidSync");

        Directory.CreateDirectory(settingsFolder);
        _settingsPath = Path.Combine(settingsFolder, "settings.json");
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
