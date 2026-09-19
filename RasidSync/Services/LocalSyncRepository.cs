using System.Text.Json;
using MySqlConnector;
using RasidSync.Models;

namespace RasidSync.Services;

public sealed class LocalSyncRepository
{
    private readonly SyncSettings _settings;

    public LocalSyncRepository(SyncSettings settings)
    {
        _settings = settings;
    }

    public async Task<long> CreateTestEventAsync()
    {
        const string sql = """
            INSERT INTO tbl_sync_send
            (
                event_uuid, source_device_uuid, entity_type, entity_uuid,
                local_id, operation_type, payload, sync_status,
                retry_count, created_at
            )
            VALUES
            (
                @event_uuid, @source_device_uuid, 'Test', @entity_uuid,
                NULL, 1, @payload, 0, 0, NOW()
            );
            SELECT LAST_INSERT_ID();
            """;

        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@event_uuid", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("@source_device_uuid", _settings.DeviceUuid);
        command.Parameters.AddWithValue("@entity_uuid", Guid.NewGuid().ToString());

        string payload = JsonSerializer.Serialize(new
        {
            message = "Windows sync test",
            deviceUuid = _settings.DeviceUuid,
            createdAt = DateTime.UtcNow
        });
        command.Parameters.AddWithValue("@payload", payload);

        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    public async Task<SyncSendRow?> GetNextEventAsync()
    {
        long lastSentId = await GetStateLongAsync("LastSentId");

        const string sql = """
            SELECT sync_id, event_uuid, source_device_uuid, entity_type,
                   entity_uuid, local_id, operation_type, payload, created_at
            FROM tbl_sync_send
            WHERE sync_id > @last_sent_id
            ORDER BY sync_id
            LIMIT 1;
            """;

        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@last_sent_id", lastSentId);

        await using MySqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        int syncIdIndex = reader.GetOrdinal("sync_id");
        int eventUuidIndex = reader.GetOrdinal("event_uuid");
        int sourceDeviceIndex = reader.GetOrdinal("source_device_uuid");
        int entityTypeIndex = reader.GetOrdinal("entity_type");
        int entityUuidIndex = reader.GetOrdinal("entity_uuid");
        int localIdIndex = reader.GetOrdinal("local_id");
        int operationTypeIndex = reader.GetOrdinal("operation_type");
        int payloadIndex = reader.GetOrdinal("payload");
        int createdAtIndex = reader.GetOrdinal("created_at");

        return new SyncSendRow
        {
            SyncId = reader.GetInt64(syncIdIndex),
            EventUuid = reader.GetString(eventUuidIndex),
            SourceDeviceUuid = reader.GetString(sourceDeviceIndex),
            EntityType = reader.GetString(entityTypeIndex),
            EntityUuid = reader.GetString(entityUuidIndex),
            LocalId = reader.IsDBNull(localIdIndex) ? null : reader.GetInt64(localIdIndex),
            OperationType = reader.GetInt32(operationTypeIndex),
            Payload = reader.IsDBNull(payloadIndex) ? "{}" : reader.GetString(payloadIndex),
            CreatedAt = reader.GetDateTime(createdAtIndex)
        };
    }

    public async Task MarkSentAsync(long syncId)
    {
        const string sql = """
            UPDATE tbl_sync_send
            SET sync_status = 2,
                sent_at = NOW(),
                last_attempt_at = NOW(),
                last_error = NULL
            WHERE sync_id = @sync_id;

            INSERT INTO tbl_sync_state (state_key, state_value, updated_at)
            VALUES ('LastSentId', @sync_id, NOW())
            ON DUPLICATE KEY UPDATE
                state_value = @sync_id,
                updated_at = NOW();
            """;

        await ExecuteStatusCommandAsync(sql, syncId, null);
    }

    public async Task MarkFailedAsync(long syncId, string error)
    {
        const string sql = """
            UPDATE tbl_sync_send
            SET sync_status = 3,
                retry_count = retry_count + 1,
                last_attempt_at = NOW(),
                last_error = @last_error
            WHERE sync_id = @sync_id;
            """;

        await ExecuteStatusCommandAsync(sql, syncId, error);
    }

    private async Task<long> GetStateLongAsync(string key)
    {
        const string sql = """
            SELECT state_value
            FROM tbl_sync_state
            WHERE state_key = @state_key
            LIMIT 1;
            """;

        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@state_key", key);

        object? value = await command.ExecuteScalarAsync();
        return long.TryParse(Convert.ToString(value), out long result) ? result : 0;
    }

    private async Task ExecuteStatusCommandAsync(string sql, long syncId, string? error)
    {
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sync_id", syncId);
        command.Parameters.AddWithValue("@last_error", error ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private MySqlConnection CreateConnection()
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = _settings.LocalServer,
            Port = _settings.LocalPort,
            Database = _settings.LocalDatabase,
            UserID = _settings.LocalUsername,
            Password = _settings.LocalPassword,
            ConnectionTimeout = 10,
            DefaultCommandTimeout = 30,
            AllowUserVariables = true
        };

        return new MySqlConnection(builder.ConnectionString);
    }
}
