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
        const string sql = """
            SELECT s.sync_id, s.event_uuid, s.source_device_uuid, s.entity_type,
                   s.entity_uuid, s.local_id, s.operation_type, s.payload, s.created_at
            FROM tbl_sync_send s
            WHERE s.sync_status IN (0, 3)
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM tbl_sync_send old
                  WHERE old.entity_uuid = s.entity_uuid
                    AND old.sync_id < s.sync_id
                    AND old.sync_status = 5
              )
            ORDER BY s.sync_id
            LIMIT 1;
            """;

        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);

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

            UPDATE tbl_sync_errors
            SET resolution_status=1, resolved_at=NOW(), updated_at=NOW()
            WHERE direction='Send' AND related_id=@sync_id AND resolution_status IN (0, 3);
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

    /// <summary>
    /// الخطأ التجاري يحتاج قرارًا بشريًا، لذلك لا نعيده تلقائيًا ونكمل الكيانات الأخرى.
    /// </summary>
    public async Task MarkBlockedAsync(SyncSendRow row, string errorCode, string error)
    {
        const string sql = """
            UPDATE tbl_sync_send
            SET sync_status=5, retry_count=retry_count+1,
                last_attempt_at=NOW(), last_error=@last_error
            WHERE sync_id=@sync_id;

            INSERT INTO tbl_sync_errors
            (
                direction, related_id, event_uuid, entity_type, entity_uuid,
                local_id, operation_type, error_code, error_message,
                error_details, resolution_status, requires_support,
                created_at, updated_at
            )
            VALUES
            (
                'Send', @sync_id, @event_uuid, @entity_type, @entity_uuid,
                @local_id, @operation_type, @error_code, @last_error,
                @payload, 0, 0, NOW(), NOW()
            );
            """;

        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sync_id", row.SyncId);
        command.Parameters.AddWithValue("@event_uuid", row.EventUuid);
        command.Parameters.AddWithValue("@entity_type", row.EntityType);
        command.Parameters.AddWithValue("@entity_uuid", row.EntityUuid);
        command.Parameters.AddWithValue("@local_id", row.LocalId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@operation_type", row.OperationType);
        command.Parameters.AddWithValue("@error_code", errorCode);
        command.Parameters.AddWithValue("@last_error", error);
        command.Parameters.AddWithValue("@payload", row.Payload);
        await command.ExecuteNonQueryAsync();
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
