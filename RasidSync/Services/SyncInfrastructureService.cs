using MySqlConnector;
using RasidSync.Models;

namespace RasidSync.Services;

/// <summary>
/// ينشئ جداول إدارة أخطاء وصلاحيات المزامنة داخل قاعدة البيانات المحلية نفسها.
/// التنفيذ آمن عند التكرار لأن الجداول تستخدم IF NOT EXISTS.
/// </summary>
public sealed class SyncInfrastructureService
{
    private readonly SyncSettings _settings;

    public SyncInfrastructureService(SyncSettings settings) => _settings = settings;

    public async Task EnsureCreatedAsync()
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS tbl_sync_errors
            (
                error_id BIGINT NOT NULL AUTO_INCREMENT,
                direction VARCHAR(10) NOT NULL,
                related_id BIGINT NOT NULL,
                event_uuid VARCHAR(36) NULL,
                entity_type VARCHAR(30) NOT NULL,
                entity_uuid VARCHAR(36) NOT NULL,
                local_id BIGINT NULL,
                operation_type INT NOT NULL,
                error_code VARCHAR(60) NOT NULL,
                error_message TEXT NOT NULL,
                error_details LONGTEXT NULL,
                resolution_status INT NOT NULL DEFAULT 0,
                requires_support TINYINT NOT NULL DEFAULT 0,
                created_at DATETIME NOT NULL,
                updated_at DATETIME NOT NULL,
                resolved_at DATETIME NULL,
                PRIMARY KEY (error_id),
                KEY ix_sync_errors_open (resolution_status, created_at),
                KEY ix_sync_errors_entity (entity_uuid, resolution_status),
                KEY ix_sync_errors_related (direction, related_id)
            ) ENGINE=MyISAM DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

            CREATE TABLE IF NOT EXISTS tbl_sync_permissions
            (
                permission_key VARCHAR(60) NOT NULL,
                permission_enabled TINYINT NOT NULL DEFAULT 0,
                password_hash VARCHAR(128) NULL,
                password_salt VARCHAR(64) NULL,
                updated_at DATETIME NOT NULL,
                PRIMARY KEY (permission_key)
            ) ENGINE=MyISAM DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

            INSERT IGNORE INTO tbl_sync_permissions
                (permission_key, permission_enabled, updated_at)
            VALUES
                ('CancelSyncEvent', 0, NOW()),
                ('MergeInvoiceIdentity', 0, NOW());
            """;

        await using MySqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
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
            AllowUserVariables = true
        };
        return new MySqlConnection(builder.ConnectionString);
    }
}
