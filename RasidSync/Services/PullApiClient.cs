using System.Text.Json;
using RasidSync.Models;

namespace RasidSync.Services;

public sealed class PullApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SyncSettings _settings;

    public PullApiClient(SyncSettings settings)
    {
        _settings = settings;
    }

    public async Task<PullSyncResponse> PullAsync(long afterId, int limit = 100)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri(_settings.ApiUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        string requestUrl =
            $"api/sync2/pull?afterId={afterId}" +
            $"&deviceUuid={Uri.EscapeDataString(_settings.DeviceUuid)}" +
            $"&limit={limit}";

        // تثبيت رؤوس تعريف الجهاز على طلب الاستقبال نفسه.
        using var message = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        message.Headers.TryAddWithoutValidation("X-Database", _settings.OnlineDatabase);
        message.Headers.TryAddWithoutValidation("X-Device-Name", Environment.MachineName);
        message.Headers.TryAddWithoutValidation("X-App-Version", DeviceAuthorizationClient.AppVersion);

        using HttpResponseMessage response = await client.SendAsync(message);
        string responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"السيرفر أعاد {(int)response.StatusCode}: {responseText}");

        PullSyncResponse? result = JsonSerializer.Deserialize<PullSyncResponse>(
            responseText,
            JsonOptions);

        return result ?? throw new InvalidOperationException("رد الاستقبال من السيرفر غير مفهوم.");
    }
}
