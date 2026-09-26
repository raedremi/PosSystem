using MySqlConnector;
using RasidSync.Models;

namespace RasidSync.Services;

/// <summary>
/// يحفظ الحركات المستقبلة ويتقدم بالمؤشر فقط بعد نجاح تطبيق الحركة.
/// </summary>
public sealed class ReceiveSyncRepository
{
    private readonly SyncSettings _settings;

    public ReceiveSyncRepository(SyncSettings settings) => _settings = settings;

    public Task<long> GetLastReceivedIdAsync() => GetStateLongAsync("LastReceivedId");

    public async Task SaveEventsAsync(IReadOnlyCollection<PulledSyncEvent> events)
    {
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();

        foreach (PulledSyncEvent item in events.OrderBy(x => x.ServerEventId))
        {
            const string sql = """
                INSERT IGNORE INTO tbl_sync_receive
                (
                    server_event_id, event_uuid, source_device_uuid,
                    entity_type, entity_uuid, operation_type,
                    payload, apply_status, received_at, retry_count
                )
                VALUES
                (
                    @server_event_id, @event_uuid, @source_device_uuid,
                    @entity_type, @entity_uuid, @operation_type,
                    @payload, 0, NOW(), 0
                );
                """;

            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@server_event_id", item.ServerEventId);
            command.Parameters.AddWithValue("@event_uuid", item.EventUuid);
            command.Parameters.AddWithValue("@source_device_uuid", item.SourceDeviceUuid);
            command.Parameters.AddWithValue("@entity_type", item.EntityType);
            command.Parameters.AddWithValue("@entity_uuid", item.EntityUuid);
            command.Parameters.AddWithValue("@operation_type", item.OperationType);
            command.Parameters.AddWithValue("@payload", item.Payload.GetRawText());
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task<int?> GetApplyStatusAsync(long serverEventId)
    {
        const string sql = "SELECT apply_status FROM tbl_sync_receive WHERE server_event_id=@id LIMIT 1;";
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", serverEventId);
        object? value = await command.ExecuteScalarAsync();
        return value is null || value == DBNull.Value ? null : Convert.ToInt32(value);
    }

    public Task MarkApplyingAsync(long serverEventId) => ExecuteEventUpdateAsync(
        "UPDATE tbl_sync_receive SET apply_status=1, last_error=NULL WHERE server_event_id=@id;",
        serverEventId);

    public async Task MarkFailedAsync(
        PulledSyncEvent item, string errorCode, string error)
    {
        const string sql = """
            UPDATE tbl_sync_receive
            SET apply_status=3, retry_count=retry_count+1, last_error=@error
            WHERE server_event_id=@id;

            INSERT INTO tbl_sync_errors
            (
                direction, related_id, event_uuid, entity_type, entity_uuid,
                local_id, operation_type, error_code, error_message,
                error_details, resolution_status, requires_support,
                created_at, updated_at
            )
            SELECT
                'Receive', @id, @event_uuid, @entity_type, @entity_uuid,
                @local_id, @operation_type, @error_code, @error,
                @payload, 0, 0, NOW(), NOW()
            FROM DUAL
            WHERE NOT EXISTS
            (
                SELECT 1 FROM tbl_sync_errors
                WHERE direction='Receive' AND related_id=@id
                  AND resolution_status IN (0, 3)
            );
            """;

        await ExecuteEventErrorAsync(sql, item, errorCode, error);
    }

    public async Task<bool> HasBlockedEntityAsync(string entityUuid, long beforeEventId)
    {
        const string sql = """
            SELECT COUNT(*) FROM tbl_sync_receive
            WHERE entity_uuid=@entity_uuid AND server_event_id<@event_id AND apply_status=5;
            """;
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@entity_uuid", entityUuid);
        command.Parameters.AddWithValue("@event_id", beforeEventId);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    public async Task MarkBlockedAndAdvanceAsync(
        PulledSyncEvent item, string errorCode, string error)
    {
        const string sql = """
            UPDATE tbl_sync_receive
            SET apply_status=5, retry_count=retry_count+1, last_error=@error
            WHERE server_event_id=@id;

            INSERT INTO tbl_sync_errors
            (
                direction, related_id, event_uuid, entity_type, entity_uuid,
                local_id, operation_type, error_code, error_message,
                error_details, resolution_status, requires_support,
                created_at, updated_at
            )
            SELECT
                'Receive', @id, @event_uuid, @entity_type, @entity_uuid,
                @local_id, @operation_type, @error_code, @error,
                @payload, 0, 0, NOW(), NOW()
            FROM DUAL
            WHERE NOT EXISTS
            (
                SELECT 1 FROM tbl_sync_errors
                WHERE direction='Receive' AND related_id=@id
                  AND resolution_status IN (0, 3)
            );
            """;

        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await ExecuteEventErrorAsync(connection, sql, item, errorCode, error);
        await AdvanceCursorAsync(connection, item.ServerEventId);
    }

    public async Task MarkAppliedAndAdvanceAsync(long serverEventId)
    {
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();

        await using (var command = new MySqlCommand(
            "UPDATE tbl_sync_receive SET apply_status=2, applied_at=NOW(), last_error=NULL WHERE server_event_id=@id;", connection))
        {
            command.Parameters.AddWithValue("@id", serverEventId);
            await command.ExecuteNonQueryAsync();
        }

        await AdvanceCursorAsync(connection, serverEventId);
    }

    public async Task AdvanceCursorAsync(long nextCursor)
    {
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await AdvanceCursorAsync(connection, nextCursor);
    }

    private static async Task AdvanceCursorAsync(MySqlConnection connection, long nextCursor)
    {
        const string sql = """
            INSERT INTO tbl_sync_state (state_key, state_value, updated_at)
            VALUES ('LastReceivedId', @value, NOW())
            ON DUPLICATE KEY UPDATE state_value=@value, updated_at=NOW();
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@value", nextCursor);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteEventErrorAsync(
        string sql, PulledSyncEvent item, string errorCode, string error)
    {
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await ExecuteEventErrorAsync(connection, sql, item, errorCode, error);
    }

    private static async Task ExecuteEventErrorAsync(
        MySqlConnection connection, string sql, PulledSyncEvent item,
        string errorCode, string error)
    {
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", item.ServerEventId);
        command.Parameters.AddWithValue("@event_uuid", item.EventUuid);
        command.Parameters.AddWithValue("@entity_type", item.EntityType);
        command.Parameters.AddWithValue("@entity_uuid", item.EntityUuid);
        command.Parameters.AddWithValue("@local_id", item.LocalId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@operation_type", item.OperationType);
        command.Parameters.AddWithValue("@error_code", errorCode);
        command.Parameters.AddWithValue("@error", error);
        command.Parameters.AddWithValue("@payload", item.Payload.GetRawText());
        await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteEventUpdateAsync(string sql, long serverEventId, string? error = null)
    {
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", serverEventId);
        if (error is not null)
            command.Parameters.AddWithValue("@error", error.Length > 60000 ? error[..60000] : error);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> GetStateLongAsync(string key)
    {
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "SELECT state_value FROM tbl_sync_state WHERE state_key=@key LIMIT 1;", connection);
        command.Parameters.AddWithValue("@key", key);
        object? value = await command.ExecuteScalarAsync();
        return long.TryParse(Convert.ToString(value), out long result) ? result : 0;
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
            DefaultCommandTimeout = 120
        };
        return new MySqlConnection(builder.ConnectionString);
    }
}
