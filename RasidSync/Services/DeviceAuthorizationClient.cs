using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using RasidSync.Models;

namespace RasidSync.Services;

/// <summary>
/// يفحص صلاحية هذا الجهاز قبل أي إرسال أو استقبال.
/// </summary>
public sealed class DeviceAuthorizationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SyncSettings _settings;

    public DeviceAuthorizationClient(SyncSettings settings) => _settings = settings;

    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public async Task<SyncDeviceCheckResponse> CheckAsync()
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri(_settings.ApiUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.Add("X-Database", _settings.OnlineDatabase);

        var request = new SyncDeviceCheckRequest
        {
            DeviceUuid = _settings.DeviceUuid,
            DeviceName = Environment.MachineName,
            AppVersion = AppVersion
        };

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "api/sync2/check-device", request, JsonOptions);
        string responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"فشل فحص صلاحية الجهاز ({(int)response.StatusCode}): {responseText}");

        SyncDeviceCheckResponse? result = JsonSerializer.Deserialize<SyncDeviceCheckResponse>(
            responseText, JsonOptions);

        return result ?? throw new InvalidOperationException(
            "رد فحص صلاحية الجهاز غير مفهوم.");
    }

    public async Task EnsureAllowedAsync()
    {
        SyncDeviceCheckResponse result = await CheckAsync();
        if (!result.Allowed)
            throw new InvalidOperationException(result.Message);
    }

    public static void AddDeviceHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Device-Name", Environment.MachineName);
        client.DefaultRequestHeaders.Add("X-App-Version", AppVersion);
    }
}
