using System.Net.Http.Json;
using System.Text.Json;
using RasidSync.Models;

namespace RasidSync.Services;

public sealed class SyncApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SyncSettings _settings;

    public SyncApiClient(SyncSettings settings)
    {
        _settings = settings;
    }

    public async Task<PushSyncResponse> PushAsync(SyncSendRow row)
    {
        using JsonDocument payloadDocument = JsonDocument.Parse(row.Payload);
        var request = new PushSyncRequest
        {
            EventUuid = row.EventUuid,
            SourceDeviceUuid = row.SourceDeviceUuid,
            EntityType = row.EntityType,
            EntityUuid = row.EntityUuid,
            LocalId = row.LocalId,
            OperationType = row.OperationType,
            Payload = payloadDocument.RootElement.Clone(),
            CreatedAt = row.CreatedAt
        };

        using var client = new HttpClient
        {
            BaseAddress = new Uri(_settings.ApiUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Add("X-Database", _settings.OnlineDatabase);
        DeviceAuthorizationClient.AddDeviceHeaders(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "api/sync2/push-test",
            request,
            JsonOptions);

        string responseText = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"السيرفر أعاد {(int)response.StatusCode}: {responseText}");

        PushSyncResponse? result = JsonSerializer.Deserialize<PushSyncResponse>(
            responseText,
            JsonOptions);

        if (result is null || !result.Success)
            throw new InvalidOperationException(result?.Message ?? "رد السيرفر غير مفهوم.");

        return result;
    }
}
