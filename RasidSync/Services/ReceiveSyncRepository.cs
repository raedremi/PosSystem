using MySqlConnector;
using RasidSync.Models;

namespace RasidSync.Services;

public sealed class ReceiveSyncRepository
{
    private readonly SyncSettings _settings;

    public ReceiveSyncRepository(SyncSettings settings)
    {
        _settings = settings;
    }

    public Task<long> GetLastReceivedIdAsync()
    {
        return GetStateLongAsync("LastReceivedId");
    }

    public async Task SaveEventsAndCursorAsync(
        IReadOnlyCollection<PulledSyncEvent> events,
        long nextCursor)
    {
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();

        foreach (PulledSyncEvent item in events.OrderBy(x => x.ServerEventId))
        {
            const string insertSql = """
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

            await using var insertCommand = new MySqlCommand(insertSql, connection);
            insertCommand.Parameters.AddWithValue("@server_event_id", item.ServerEventId);
            insertCommand.Parameters.AddWithValue("@event_uuid", item.EventUuid);
            insertCommand.Parameters.AddWithValue("@source_device_uuid", item.SourceDeviceUuid);
            insertCommand.Parameters.AddWithValue("@entity_type", item.EntityType);
            insertCommand.Parameters.AddWithValue("@entity_uuid", item.EntityUuid);
            insertCommand.Parameters.AddWithValue("@operation_type", item.OperationType);
            insertCommand.Parameters.AddWithValue("@payload", item.Payload.GetRawText());
            await insertCommand.ExecuteNonQueryAsync();
        }

        const string stateSql = """
            INSERT INTO tbl_sync_state (state_key, state_value, updated_at)
            VALUES ('LastReceivedId', @next_cursor, NOW())
            ON DUPLICATE KEY UPDATE
                state_value = @next_cursor,
                updated_at = NOW();
            """;

        await using var stateCommand = new MySqlCommand(stateSql, connection);
        stateCommand.Parameters.AddWithValue("@next_cursor", nextCursor);
        await stateCommand.ExecuteNonQueryAsync();
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
            DefaultCommandTimeout = 30
        };

        return new MySqlConnection(builder.ConnectionString);
    }
}
