using System.Text.Json;

namespace RasidSync.Services;

/// <summary>
/// يحفظ UUID في ملف خاص بالكمبيوتر، خارج مجلد البرنامج القابل للنسخ.
/// بهذه الطريقة لا تحصل الفروع المختلفة على UUID واحد عند نسخ RasidSync.
/// </summary>
public sealed class DeviceIdentityService
{
    private readonly string _identityPath;
    private readonly string _legacySettingsPath;

    public DeviceIdentityService()
    {
        string localFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RasidSync");

        Directory.CreateDirectory(localFolder);
        _identityPath = Path.Combine(localFolder, "device_uuid.txt");
        _legacySettingsPath = Path.Combine(localFolder, "settings.json");
    }

    public async Task<string> GetOrCreateAsync()
    {
        // الهوية التي أُنشئت سابقًا لهذا Windows هي المرجع الأساسي.
        if (File.Exists(_identityPath))
        {
            string savedUuid = (await File.ReadAllTextAsync(_identityPath)).Trim();
            if (Guid.TryParse(savedUuid, out Guid parsed))
                return parsed.ToString();
        }

        // نحافظ على UUID القديم إذا كانت نسخة سابقة خزّنته في LocalAppData.
        string? legacyUuid = await ReadLegacyUuidAsync();
        string deviceUuid = Guid.TryParse(legacyUuid, out Guid oldUuid)
            ? oldUuid.ToString()
            : Guid.NewGuid().ToString();

        await File.WriteAllTextAsync(_identityPath, deviceUuid);
        return deviceUuid;
    }

    private async Task<string?> ReadLegacyUuidAsync()
    {
        if (!File.Exists(_legacySettingsPath))
            return null;

        try
        {
            string json = await File.ReadAllTextAsync(_legacySettingsPath);
            using JsonDocument document = JsonDocument.Parse(json);

            return document.RootElement.TryGetProperty("DeviceUuid", out JsonElement value)
                ? value.GetString()
                : null;
        }
        catch
        {
            // تلف ملف قديم لا يمنع إنشاء هوية جديدة وسليمة.
            return null;
        }
    }
}
