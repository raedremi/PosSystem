using System.Security.Cryptography;
using System.Text;
using Dapper;
using MySqlConnector;
using RasidSync.Models;

namespace RasidSync.Services;

/// <summary>قراءة سجلات المزامنة ومعالجة الخطأ بقرارات واضحة.</summary>
public sealed class SyncLogRepository
{
    private readonly SyncSettings _settings;
    public SyncLogRepository(SyncSettings settings) => _settings = settings;

    public Task<List<SyncLogRow>> GetSendRowsAsync(DateTime from, DateTime toExclusive) => ReadLogsAsync("""
        SELECT sync_id Id, entity_type EntityType, entity_uuid EntityUuid,
               local_id LocalId, operation_type OperationType,
               sync_status Status, retry_count RetryCount,
               created_at CreatedAt, last_error Error
        FROM tbl_sync_send
        WHERE created_at >= @from AND created_at < @to
        ORDER BY sync_id DESC LIMIT 2000;
        """, from, toExclusive);

    public Task<List<SyncLogRow>> GetReceiveRowsAsync(DateTime from, DateTime toExclusive) => ReadLogsAsync("""
        SELECT server_event_id Id, entity_type EntityType, entity_uuid EntityUuid,
               NULL LocalId, operation_type OperationType,
               apply_status Status, retry_count RetryCount,
               received_at CreatedAt, last_error Error
        FROM tbl_sync_receive
        WHERE received_at >= @from AND received_at < @to
        ORDER BY server_event_id DESC LIMIT 2000;
        """, from, toExclusive);

    public async Task<List<SyncErrorRow>> GetErrorsAsync(DateTime from, DateTime toExclusive)
    {
        const string sql = """
            SELECT error_id ErrorId, direction Direction, related_id RelatedId,
                   entity_type EntityType, entity_uuid EntityUuid, local_id LocalId,
                   operation_type OperationType, error_code ErrorCode,
                   error_message ErrorMessage, error_details ErrorDetails,
                   resolution_status ResolutionStatus, created_at CreatedAt
            FROM tbl_sync_errors
            WHERE created_at >= @from AND created_at < @to
            ORDER BY error_id DESC LIMIT 2000;
            """;
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        return (await connection.QueryAsync<SyncErrorRow>(sql, new { from, to = toExclusive })).ToList();
    }

    public async Task RetryAsync(SyncErrorRow error)
    {
        if (!string.Equals(error.Direction, "Send", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("إعادة محاولة أخطاء الاستقبال ستضاف مع مرحلة معالجة الاستقبال اليدوية.");

        await ExecuteAsync("""
            UPDATE tbl_sync_send SET sync_status=0, last_error=NULL
            WHERE sync_id=@related_id;
            UPDATE tbl_sync_errors SET resolution_status=3, updated_at=NOW()
            WHERE error_id=@error_id;
            """, error);
    }

    public async Task CancelAsync(SyncErrorRow error, string password)
    {
        if (!await VerifyPermissionAsync("CancelSyncEvent", password))
            throw new UnauthorizedAccessException("كلمة مرور الدعم أو صلاحية إلغاء الحركة غير صحيحة.");
        if (!string.Equals(error.Direction, "Send", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("إلغاء أخطاء الاستقبال غير متاح في هذه المرحلة.");

        await ExecuteAsync("""
            UPDATE tbl_sync_send SET sync_status=4, last_error='تم إلغاء الحركة بقرار إداري.'
            WHERE sync_id=@related_id;
            UPDATE tbl_sync_errors SET resolution_status=2, resolved_at=NOW(), updated_at=NOW()
            WHERE error_id=@error_id;
            """, error);
    }

    public async Task<bool> VerifyPermissionAsync(string key, string password)
    {
        const string sql = """
            SELECT permission_enabled, password_hash, password_salt
            FROM tbl_sync_permissions WHERE permission_key=@key LIMIT 1;
            """;
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@key", key);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return false;

        int enabledIndex = reader.GetOrdinal("permission_enabled");
        int hashIndex = reader.GetOrdinal("password_hash");
        int saltIndex = reader.GetOrdinal("password_salt");

        if (!reader.GetBoolean(enabledIndex) ||
            reader.IsDBNull(hashIndex) || reader.IsDBNull(saltIndex))
            return false;

        string expected = reader.GetString(hashIndex);
        string salt = reader.GetString(saltIndex);
        string actual = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(salt + password)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected.ToUpperInvariant()),
            Encoding.UTF8.GetBytes(actual));
    }

    private async Task<List<SyncLogRow>> ReadLogsAsync(string sql, DateTime from, DateTime toExclusive)
    {
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        return (await connection.QueryAsync<SyncLogRow>(sql, new { from, to = toExclusive })).ToList();
    }

    private async Task ExecuteAsync(string sql, SyncErrorRow error)
    {
        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@related_id", error.RelatedId);
        command.Parameters.AddWithValue("@error_id", error.ErrorId);
        await command.ExecuteNonQueryAsync();
    }

    private MySqlConnection CreateConnection()
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = _settings.LocalServer, Port = _settings.LocalPort,
            Database = _settings.LocalDatabase, UserID = _settings.LocalUsername,
            Password = _settings.LocalPassword, AllowUserVariables = true
        };
        return new MySqlConnection(builder.ConnectionString);
    }
}
